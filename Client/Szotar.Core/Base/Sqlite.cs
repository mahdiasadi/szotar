using System;
using System.Data.Common;
using System.Reflection;
using System.IO;	
using IO = System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections;
using System.Text;

namespace Szotar.Sqlite {
	
	[global::System.Serializable]
	public class DatabaseVersionException : Exception {
		public DatabaseVersionException() { }
		public DatabaseVersionException(string message) : base(message) { }
		public DatabaseVersionException(string message, Exception inner) : base(message, inner) { }
		protected DatabaseVersionException(
		  System.Runtime.Serialization.SerializationInfo info,
		  System.Runtime.Serialization.StreamingContext context)
			: base(info, context) { }
	}

	public abstract class SqliteDatabase : SqliteObject {
		string path;

		public SqliteDatabase(string path)
			: base(OpenDatabase(path))
		{
			this.path = path;
			conn.Open();

			Init();
		}
		
		static DbConnection OpenDatabase(string path) {
			
			string dir = IO.Path.GetDirectoryName(path);
			if(!IO.Directory.Exists(dir))
				IO.Directory.CreateDirectory(path);
#if !MONO
			return new System.Data.SQLite.SQLiteConnection("Data Source=" + path);
#else
			return new Mono.Data.Sqlite.SqliteConnection("Data Source=" + path);
#endif			
		}

		protected string Path {
			get { return path; }
		}

		private void Init() {
			int appSchemaVer = this.ApplicationSchemaVersion();
			Debug.Assert(appSchemaVer >= 1, "Desired SQLite database schema version should be greater than 0");

			int dbVer = GetDatabaseSchemaVersion();
			if (dbVer < appSchemaVer) {
				using (var txn = conn.BeginTransaction()) {
					UpgradeSchema(dbVer, appSchemaVer);
					SetDatabaseSchemaVersion(appSchemaVer);
					txn.Commit();
				}
			} else if (dbVer > appSchemaVer) {
				throw new DatabaseVersionException("The SQLite database is created by a newer version of this application.");
			}
		}

		protected abstract int ApplicationSchemaVersion();
		protected virtual void UpgradeSchema(int fromVersion, int toVersion) { UpgradeSchemaInIncrements(fromVersion, toVersion); }
		protected virtual void IncrementalUpgradeSchema(int toVersion) { }

		protected bool TableExists(string name) {
			return conn.GetSchema("Tables").Select(string.Format("Table_Name = '{0}'", name)).Length > 0;
		}

		protected int GetDatabaseSchemaVersion() {
			if (TableExists("Info")) {
				using (var cmd = conn.CreateCommand()) {
					cmd.CommandText = "TYPES integer; SELECT Info.Value FROM Info WHERE Info.Name = 'Version'";

					return Convert.ToInt32(cmd.ExecuteScalar());
				}
			} else {
				return 0;
			}
		}

		protected void SetDatabaseSchemaVersion(int version) {
			using (var txn = conn.BeginTransaction()) {
				if (!TableExists("Info")) {
					using (var cmd = conn.CreateCommand()) {
						cmd.CommandText = "CREATE TABLE Info (Name TEXT PRIMARY KEY, Value TEXT)";
						cmd.ExecuteNonQuery();
					}
				}

				using (var cmd = conn.CreateCommand()) {
					cmd.CommandText = "INSERT INTO Info (Name, Value) VALUES ('Version', ?)";
					var param = cmd.CreateParameter();
					cmd.Parameters.Add(param);
					param.Value = version.ToString();
					cmd.ExecuteNonQuery();
				}

				txn.Commit();
			}
		}

		protected void UpgradeSchemaInIncrements(int fromVersion, int toVersion) {
			if (fromVersion >= toVersion)
				throw new ArgumentException("Can't downgrade the application database. What is happening?", "fromVersion");

			for (; fromVersion < toVersion; ++fromVersion)
				IncrementalUpgradeSchema(fromVersion + 1);
		}

		protected override void Dispose(bool disposing) {
			if (disposing)
				conn.Dispose(); 
			
			base.Dispose(disposing);
		}
	}

	public abstract class SqliteObject : IDisposable {
		protected DbConnection conn;

		public SqliteObject(SqliteObject other) {
			this.conn = other.conn;
		}

		protected SqliteObject(DbConnection connection) {
			this.conn = connection;
		}

		/// <summary>
		/// Get the ID of the last inserted row. This is equal to the primary key of that row, if one exists.
		/// If multiple threads are modifying the database, this value is unpredictable. So, please, don't do that.
		/// </summary>
		protected long GetLastInsertRowID() {
			var lastInsertCommand = conn.CreateCommand();
			lastInsertCommand.CommandText = "SELECT last_insert_rowid()";

			return (long)lastInsertCommand.ExecuteScalar();
		}

		protected void ExecuteSQL(string sql, params object[] parameters) {
			using (DbCommand command = conn.CreateCommand()) {
				command.CommandText = sql;

				foreach (object p in parameters)
					AddParameter(command, p);

				command.ExecuteNonQuery();
			}
		}

		protected object Select(string sql, params object[] parameters) {
			using (DbCommand command = conn.CreateCommand()) {
				command.CommandText = sql;

				foreach (object p in parameters)
					AddParameter(command, p);

				var res = command.ExecuteScalar();
				if (res is DBNull)
					return null;
				return res;
			}
		}
		
		protected DbDataReader SelectReader(string sql, params object[] parameters) {
			using (DbCommand command = conn.CreateCommand()) {
				command.CommandText = sql;

				foreach (object p in parameters) {
					var param = command.CreateParameter();
					param.Value = p;
					command.Parameters.Add(param);
				}

				return command.ExecuteReader();
			}
		}

		protected void AddParameter(DbCommand command, object value) {
			var param = command.CreateParameter();
			param.Value = value;
			command.Parameters.Add(param);
		}

		protected DbConnection Connection { get { return conn; } }

		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing) {
		}
	}

	public class WordListDeletedEventArgs : EventArgs {
		public long SetID { get; set; }
	}

	public class SqliteDataStore : SqliteDatabase {
		Dictionary<long, NullWeakReference<SqliteWordList>> wordLists = new Dictionary<long, NullWeakReference<SqliteWordList>>();

		public SqliteDataStore(string path)
			: base(path) {
		}

		protected override void UpgradeSchema(int fromVersion, int toVersion) {
			UpgradeSchemaInIncrements(fromVersion, toVersion);
		}

		protected override int ApplicationSchemaVersion() {
			return 1;
		}

		protected override void IncrementalUpgradeSchema(int toVersion) {
			if (toVersion == 1)
				InitDatabase();
			else
				throw new ArgumentOutOfRangeException("toVersion");
		}

		private void InitDatabase() {
			using (var txn = conn.BeginTransaction()) {
				ExecuteSQL("CREATE TABLE VocabItems (id INTEGER PRIMARY KEY AUTOINCREMENT, Phrase TEXT NOT NULL, Translation TEXT NOT NULL, SetID INTEGER NOT NULL, ListPosition INTEGER NOT NULL, TimesTried INTEGER NOT NULL, TimesFailed INTEGER NOT NULL)");
				ExecuteSQL("CREATE INDEX VocabItems_IndexP ON VocabItems (Phrase)");
				ExecuteSQL("CREATE INDEX VocabItems_IndexT ON VocabItems (Translation)");
				ExecuteSQL("CREATE INDEX VocabItems_IndexS ON VocabItems (SetID)");
				ExecuteSQL("CREATE INDEX VocabItems_IndexSO ON VocabItems (SetID, ListPosition)");
				//ExecuteSQL("CREATE INDEX VocabItems_IndexK ON VocabItems (Knowledge)");

				ExecuteSQL("CREATE TABLE Sets (id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Author TEXT, Language TEXT, Url TEXT, Created Date)");
				ExecuteSQL("CREATE INDEX Sets_Index ON Sets (id)");

				ExecuteSQL("CREATE TABLE SetProperties (SetID INTEGER NOT NULL, Property TEXT, Value TEXT)");
				ExecuteSQL("CREATE INDEX SetProperties_Index ON SetProperties (SetID, Property)");

				ExecuteSQL("CREATE TABLE SetMemberships (ChildID INTEGER NOT NULL, ParentID INTEGER NOT NULL)");
				ExecuteSQL("CREATE INDEX SetMemberships_Index ON SetMemberships (ChildID, ParentID)");

				txn.Commit();
			}
		}

		/// <param name="setID">The SetID of the word list</param>
		/// <returns>An existing SqliteWordList instance, if one exists, otherwise a newly-created SqliteWordList.</returns>
		public SqliteWordList GetWordList(long setID) {
			SqliteWordList wl = null;
			NullWeakReference<SqliteWordList> list;

			if (wordLists.TryGetValue(setID, out list))
				wl = list.Target;

			if(wl != null)
				return wl;

			wl = new SqliteWordList(this, setID);
			wordLists[setID] = new NullWeakReference<SqliteWordList>(wl);
			return wl;
		}

		public SqliteWordList CreateSet(string name, string author, string language, string url, DateTime? date) {
			long setID;

			using (var txn = Connection.BeginTransaction()) {
				ExecuteSQL("INSERT INTO Sets (Name, Author, Language, Url, Created) VALUES (?, ?, ?, ?, ?)",
					name, author, language, url, date.HasValue ? (object)date.Value : (object)DBNull.Value);

				setID = GetLastInsertRowID();
				txn.Commit();
			}

			var wl = new SqliteWordList(this, setID);
			wordLists[setID] = new NullWeakReference<SqliteWordList>(wl);
			return wl;
		}

		public IEnumerable<ListInfo> GetAllSets() {
			using (var reader = this.SelectReader("TYPES Integer, Text, Text, Text, Text, Date, Integer; SELECT id, Name, Author, Language, Url, Created, (SELECT count(*) FROM VocabItems WHERE SetID = Sets.id) FROM Sets ORDER BY id ASC")) {
				while (reader.Read()) {
					var list = new ListInfo();
					list.ID = reader.GetInt64(0);
					list.Name = reader.GetString(1); //Can't be null
					if (!reader.IsDBNull(2))
						list.Author = reader.GetString(2);
					if (!reader.IsDBNull(3))
						list.Language = reader.GetString(3);
					if (!reader.IsDBNull(4))
						list.Url = reader.GetString(4);
					if (!reader.IsDBNull(5))
						list.Date = reader.GetDateTime(5);
					if (!reader.IsDBNull(6))
						list.TermCount = reader.GetInt64(6);

					yield return list;
				}
			}
		}

		public class WordSearchResult {
			public string Phrase { get; set; }
			public string Translation { get; set; }
			public string SetName { get; set; }
			public long SetID { get; set; }
			public int ListPosition { get; set; }
		}

		public IEnumerable<WordSearchResult> SearchAllEntries(string query) {
			var sb = new StringBuilder();

			if (string.IsNullOrEmpty(query)) {
				sb.Append("%");
			} else {
				sb.Append("%");
				foreach (char c in query) {
					switch (c) {
						//']' on its own doesn't need to be escaped.
						case '%':
						case '_':
						case '[':
							sb.Append('[').Append(c).Append(']');
							break;
						default:
							sb.Append(c);
							break;
					}
				}
				sb.Append("%");
			}

			using (var reader = this.SelectReader(
				"TYPES Integer, Text, Text, Text, Integer;" +
				"SELECT SetID, Name, Phrase, Translation, ListPosition FROM VocabItems JOIN Sets ON (VocabItems.SetID = Sets.id)" +
				"WHERE Phrase LIKE ? OR Translation LIKE ? ORDER BY SetID ASC, Phrase ASC", sb.ToString(), sb.ToString())) 
			{

				while (reader.Read()) {
					var wsr = new WordSearchResult();
					wsr.SetID = reader.GetInt64(0);
					wsr.SetName = reader.GetString(1);
					wsr.Phrase = reader.GetString(2);
					wsr.Translation = reader.GetString(3);
					wsr.ListPosition = reader.GetInt32(4);

					yield return wsr;
				}
			}
		}

		//This function also raised the ListDeleted event on the WordList, if one exists.
		public void DeleteWordList(long setID) {
			NullWeakReference<SqliteWordList> wlr;
			if (wordLists.TryGetValue(setID, out wlr))
				wordLists.Remove(setID);

			using (var txn = Connection.BeginTransaction()) {
				ExecuteSQL("DELETE FROM Sets WHERE id = ?", setID);
				ExecuteSQL("DELETE FROM VocabItems WHERE SetID = ?", setID);
				ExecuteSQL("DELETE FROM SetProperties WHERE SetID = ?", setID);
				ExecuteSQL("DELETE FROM SetMemberships WHERE ChildID = ? OR ParentID = ?", setID, setID);

				txn.Commit();
			}

			var handler = WordListDeleted;
			if (handler != null)
				handler(this, new WordListDeletedEventArgs { SetID = setID });

			if (wlr != null) {
				var wl = wlr.Target;
				if (wl != null)
					wl.RaiseDeleted();
			}
		}

		//Note: there's also a ListDeleted event on the WordList itself, which may be preferable.
		public event EventHandler<WordListDeletedEventArgs> WordListDeleted;
	}
}