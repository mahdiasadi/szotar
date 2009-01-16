﻿using System.Collections.Generic;
using System.Data.Common;
using CultureInfo = System.Globalization.CultureInfo;

namespace Szotar.Sqlite {
	public class SqliteWordList : SyncWordList {
		Worker worker;
		IList<WordListEntry> list;

		public SqliteWordList(SqliteDataStore store, long setID) {
			DataStore = store;
			SetID = setID;
			worker = new Worker(store, this);
			list = worker.GetAllEntries();
		}

		public SqliteDataStore DataStore { get;	private set; }
		public long SetID { get; private set; }

		protected override void Dispose(bool disposing) {
			if (disposing)
				worker.Dispose();

			base.Dispose(disposing);
		}

		public string Author {
			get { return (string)worker.GetWordListProperty("Author"); }
			set {
				if (value == null)
					throw new System.ArgumentNullException();
				worker.SetWordListProperty("Author", value);
			}
		}

		public string Name {
			get { return (string)worker.GetWordListProperty("Name"); }
			set {
				if (value == null)
					throw new System.ArgumentNullException();
				worker.SetWordListProperty("Name", value);
			}
		}

		public string Language {
			get { return (string)worker.GetWordListProperty("Language"); }
			set {
				if (value == null)
					throw new System.ArgumentNullException();
				worker.SetWordListProperty("Language", value);
			}
		}

		public override T GetProperty<T>(WordListEntry entry, SyncWordList.EntryProperty property) {
			return (T)worker.GetProperty(IndexOf(entry), property.ToString());
		}

		public override void SetProperty<T>(WordListEntry entry, SyncWordList.EntryProperty property, T value) {
			worker.SetProperty(IndexOf(entry), property.ToString(), value);
		}

		public override void Add(WordListEntry item) {
			Insert(Count, item);
		}

		public override void Insert(int index, WordListEntry item) {
			UndoList.Do(new Insertion(this, index, item));
		}

		public void Insert(int index, IList<WordListEntry> items) {
			UndoList.Do(new MultipleInsertion(this, index, items));
		}

		public override bool Remove(WordListEntry item) {
			bool existed = list.Contains(item);
			UndoList.Do(new Deletion(this, IndexOf(item)));
			return existed;
		}

		public override void RemoveAt(int index) {
			UndoList.Do(new Deletion(this, index));
		}

		public override void Clear() {
			UndoList.Do(new Deletion(this, EnumRange(0, list.Count - 1)));
		}

		protected IEnumerable<int> EnumRange(int from, int to) {
			while (from <= to)
				yield return from++;
		}

		public override bool Contains(WordListEntry item) {
			return list.Contains(item);
		}

		public override void CopyTo(WordListEntry[] array, int arrayIndex) {
			list.CopyTo(array, arrayIndex);
		}

		public override int Count {
			get { return list.Count; }
		}

		public override bool IsReadOnly {
			get { return false; }
		}

		public override WordListEntry this[int index] {
			get {
				return list[index];
			}
			set {
				UndoList.Do(new SetItem(this, index, value));
			}
		}

		public override int IndexOf(WordListEntry item) {
			return list.IndexOf(item);
		}

		public override IEnumerator<WordListEntry> GetEnumerator() {
			return list.GetEnumerator();
		}

		protected class Worker : SqliteObject {
			SqliteWordList list;

			DbCommand insertCommand;
			DbParameter insertCommandSetID, insertCommandListPosition, insertCommandPhrase,
				insertCommandTranslation, insertCommandTimesTried, insertCommandTimesFailed;

			public Worker(SqliteDataStore store, SqliteWordList list)
				: base(store) {
				this.list = list;

				insertCommand = Connection.CreateCommand();
				insertCommand.CommandText =
					"UPDATE VocabItems SET ListPosition = ListPosition + 1 WHERE SetID = $SetID AND ListPosition >= $ListPosition; " +
					"INSERT INTO VocabItems (Phrase, Translation, SetID, ListPosition, TimesTried, TimesFailed) " +
					"VALUES ($Phrase, $Translation, $SetID, $ListPosition, $TimesTried, $TimesFailed);";

				insertCommandSetID = insertCommand.CreateParameter();
				insertCommandSetID.ParameterName = "SetID";
				insertCommandSetID.Value = list.SetID;
				insertCommand.Parameters.Add(insertCommandSetID);

				insertCommandListPosition = insertCommand.CreateParameter();
				insertCommandListPosition.ParameterName = "ListPosition";
				insertCommand.Parameters.Add(insertCommandListPosition);

				insertCommandPhrase = insertCommand.CreateParameter();
				insertCommandPhrase.ParameterName = "Phrase";
				insertCommand.Parameters.Add(insertCommandPhrase);

				insertCommandTranslation = insertCommand.CreateParameter();
				insertCommandTranslation.ParameterName = "Translation";
				insertCommand.Parameters.Add(insertCommandTranslation);

				insertCommandTimesTried = insertCommand.CreateParameter();
				insertCommandTimesTried.ParameterName = "TimesTried";
				insertCommand.Parameters.Add(insertCommandTimesTried);

				insertCommandTimesFailed = insertCommand.CreateParameter();
				insertCommandTimesFailed.ParameterName = "TimesFailed";
				insertCommand.Parameters.Add(insertCommandTimesFailed);
			}

			protected override void Dispose(bool disposing) {
				insertCommand.Dispose();

				base.Dispose(disposing);
			}

			public void Insert(int index, WordListEntry item) {
				if (item == null)
					throw new System.ArgumentNullException();
				if (index < 0)
					throw new System.ArgumentOutOfRangeException();

				using (var txn = Connection.BeginTransaction()) {
					//ExecuteSQL("UPDATE VocabItems SET ListPosition = ListPosition + 1 WHERE SetID = ? AND ListPosition >= ?", list.SetID, index);
					//ExecuteSQL("INSERT INTO VocabItems (Phrase, Translation, SetID, ListPosition, TimesTried, TimesFailed) VALUES (?, ?, ?, ?, ?, ?)",
					//    item.Phrase, item.Translation, list.SetID, index, item.TimesTried, item.TimesFailed);

					insertCommandListPosition.Value = index;
					insertCommandPhrase.Value = item.Phrase;
					insertCommandTranslation.Value = item.Translation;
					insertCommandTimesTried.Value = item.TimesTried;
					insertCommandTimesFailed.Value = item.TimesFailed;

					insertCommand.ExecuteNonQuery();

					txn.Commit();
				}
			}

			public void Insert(int index, IList<WordListEntry> entries) {
				if (index < 0)
					throw new System.ArgumentOutOfRangeException();
				if (entries.Count == 0)
					return;

				using (var txn = Connection.BeginTransaction()) {
					foreach (WordListEntry item in entries) {
						insertCommandListPosition.Value = index;
						insertCommandPhrase.Value = item.Phrase;
						insertCommandTranslation.Value = item.Translation;
						insertCommandTimesTried.Value = item.TimesTried;
						insertCommandTimesFailed.Value = item.TimesFailed;

						insertCommand.ExecuteNonQuery();

						index++;
					}

					txn.Commit();
				}
			}

			public void RemoveAt(int index) {
				using (var txn = Connection.BeginTransaction()) {
					ExecuteSQL("DELETE From VocabItems WHERE SetID = ? AND ListPosition = ?", list.SetID, index);
					ExecuteSQL("UPDATE VocabItems SET ListPosition = ListPosition - 1 WHERE ListPosition > ?", list.SetID, index);

					txn.Commit();
				}
			}

			public void RemoveAt(int index, int count) {
				using (var txn = Connection.BeginTransaction()) {
					ExecuteSQL("DELETE From VocabItems WHERE SetID = ? AND ListPosition BETWEEN ? AND ?", list.SetID, index, index + count - 1);
					ExecuteSQL("UPDATE VocabItems SET ListPosition = ListPosition - ? WHERE SetID = ? AND ListPosition > ?", count, list.SetID, index);

					txn.Commit();
				}
			}

			public void RemoveAt(IEnumerable<int> indices) {
				if (indices == null)
					throw new System.ArgumentNullException();

				using (var txn = Connection.BeginTransaction()) {
					using (DbCommand delete = Connection.CreateCommand()) {
						delete.CommandText = "DELETE From VocabItems WHERE SetID = ? AND ListPosition = ?";
						var setIDParam = delete.CreateParameter();
						setIDParam.Value = list.SetID;
						delete.Parameters.Add(setIDParam);

						var deleteIndexParam = delete.CreateParameter();
						delete.Parameters.Add(deleteIndexParam);

						using (DbCommand update = Connection.CreateCommand()) {
							update.CommandText = "UPDATE VocabItems SET ListPosition = ListPosition - 1 WHERE SetID = ? AND ListPosition > ?";
							update.Parameters.Add(setIDParam);
							update.Parameters.Add(deleteIndexParam);

							foreach (int i in indices) {
								deleteIndexParam.Value = i;
								delete.ExecuteNonQuery();
								update.ExecuteNonQuery();
							}
						}
					}

					txn.Commit();
				}
			}

			public IList<WordListEntry> GetEntries(IEnumerable<int> indices) {
				if (indices == null)
					throw new System.ArgumentNullException();

				using (var command = Connection.CreateCommand()) {
					var sb = new System.Text.StringBuilder();
					sb.Append(@"SELECT Phrase, Translation, TimesTried, TimesFailed, 
					            ListPosition FROM VocabItems WHERE ListPosition IN (");

					int i = 0;
					foreach (int index in indices) {
						if (i++ > 0)
							sb.Append(", ");
						sb.Append(index);
					}

					sb.Append(") ORDER BY ListPosition ASC");
					command.CommandText = sb.ToString();

					using (var reader = command.ExecuteReader())
						return GetEntries(reader);
				}
			}

			public IList<WordListEntry> GetAllEntries() {
				using (var command = Connection.CreateCommand()) {
					command.CommandText = @"TYPES Text, Text, Integer, Integer; SELECT Phrase, Translation, TimesTried, TimesFailed FROM VocabItems WHERE SetID = ? ORDER BY ListPosition ASC";
					AddParameter(command, list.SetID);
					using (var reader = command.ExecuteReader())
						return GetEntries(reader);
				}
			}

			protected IList<WordListEntry> GetEntries(DbDataReader reader) {
				var result = new List<WordListEntry>();

				while (reader.Read()) {
					if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
						throw new System.Data.StrongTypingException();

					string phrase = (string)reader.GetValue(0);
					string translation = (string)reader.GetValue(1);
					long tried = (long)reader.GetValue(2);
					long failed = (long)reader.GetValue(3);

					var entry = new WordListEntry(this.list, phrase, translation, tried, failed);
					result.Add(entry);
				}

				return result;
			}

			public object GetProperty(int index, string name) {
				using (var cmd = Connection.CreateCommand()) {
					cmd.CommandText = string.Format(CultureInfo.InvariantCulture, "SELECT {0} FROM VocabItems WHERE SetID = ? AND ListPosition = ?");
					AddParameter(cmd, list.SetID);
					AddParameter(cmd, index);

					return cmd.ExecuteScalar();
				}
			}

			public void SetProperty(int index, string name, object value) {
				using (var txn = Connection.BeginTransaction()) {
					ExecuteSQL(string.Format(CultureInfo.InvariantCulture, "UPDATE VocabItems SET {0} = ? WHERE SetID = ? AND ListPosition = ?", name), value, list.SetID, index);

					txn.Commit();
				}
			}

			public void SetEntry(int index, WordListEntry item) {
				using (var txn = Connection.BeginTransaction()) {
					ExecuteSQL("UPDATE VocabItems Set Phrase = ?, Translation = ?, TimesTried = ?, TimesFailed = ? WHERE SetID = ? AND ListPosition = ?",
						item.Phrase, item.Translation, item.TimesTried, item.TimesFailed, list.SetID, index);

					txn.Commit();
				}
			}

			public void SetWordListProperty(string property, object value) {
				using (var txn = Connection.BeginTransaction()) {
					ExecuteSQL(string.Format(CultureInfo.InvariantCulture, "UPDATE Sets SET {0} = ? WHERE id = ?", property), value, list.SetID);

					txn.Commit();
				}
			}

			public object GetWordListProperty(string property) {
				return Select(string.Format(CultureInfo.InvariantCulture, "SELECT {0} FROM Sets WHERE id = ?", property), list.SetID);
			}
		}

		protected abstract class Command : ICommand {
			protected SqliteWordList owner;
			protected Worker worker;
			protected IList<WordListEntry> list;

			public Command(SqliteWordList owner) {
				this.worker = owner.worker;
				this.list = owner.list;
			}

			public abstract void Do();
			public abstract void Undo();
			public virtual void Redo() { Do(); }
			public virtual ICommand Coalesce(ICommand previous) { return null; }
			public abstract string Description { get; }
		}

		protected class SetItem : Command {
			int index;
			WordListEntry item;
			WordListEntry oldItem;

			public SetItem(SqliteWordList owner, int index, WordListEntry item)
				: base(owner) {
				this.index = index;
				this.item = item;
			}

			public override void Do() {
				oldItem = list[index];
				list[index] = item;
				worker.SetEntry(index, item);
			}

			public override void Redo() {
				Do();
			}

			public override void Undo() {
				list[index] = oldItem;
				worker.SetEntry(index, oldItem);
			}

			public override string Description {
				get { return "(Internal Operation)"; }
			}
		}

		protected class Insertion : Command {
			int index;
			WordListEntry item;

			public Insertion(SqliteWordList owner, int index, WordListEntry item)
				: base(owner) {
				this.index = index;
				this.item = item;
			}

			public override void Do() {
				worker.Insert(index, item);
				list.Insert(index, item);
			}

			public override void Redo() { Do(); }

			public override void Undo() {
				worker.RemoveAt(index);
				list.RemoveAt(index);
			}

			public override string Description {
				get { return "Inserted 1 item"; }
			}
		}

		protected class MultipleInsertion : Command {
			int index;
			IList<WordListEntry> items;

			public MultipleInsertion(SqliteWordList owner, int index, IList<WordListEntry> items)
				: base(owner) {
				this.index = index;
				this.items = items;
			}

			public override void Do() {
				worker.Insert(index, items);
				for (int i = 0; i < items.Count; ++i)
					list.Insert(index + i, items[i]);
			}

			public override void Redo() { Do(); }

			public override void Undo() {
				worker.RemoveAt(index, items.Count);
				for(int i = 0; i < items.Count; ++i); 
					list.RemoveAt(index);
			}

			public override string Description {
				get { return string.Format(CultureInfo.CurrentUICulture, "Inserted {0} items", items.Count); }
			}
		}

		protected class SetValue<T> : Command {
			int index;
			T oldValue, newValue;
			SyncWordList.EntryProperty property;

			public SetValue(SqliteWordList owner, int index, SyncWordList.EntryProperty property, T oldValue, T newValue)
				: base(owner) {
				this.index = index;
				this.oldValue = oldValue;
				this.newValue = newValue;
				this.property = property;
			}

			private void SetTo(T value) {
				worker.SetProperty(index, property.ToString(), value);
				list[index].SetProperty<T>(property, value);
			}

			public override void Do() {
				SetTo(newValue);
			}

			public override void Redo() {
				Do();
			}

			public override void Undo() {
				SetTo(oldValue);
			}

			public override string Description {
				get { return string.Format("Changed \"{0}\" to \"{1}\"", oldValue, newValue); }
			}
		}

		protected class Deletion : Command {
			List<KeyValuePair<int, WordListEntry>> items = new List<KeyValuePair<int, WordListEntry>>();
			IEnumerable<int> indices;
			IEnumerable<int> reverseIndices;

			public Deletion(SqliteWordList owner, IEnumerable<int> indices)
				: base(owner) {
				foreach (int i in indices)
					items.Add(new KeyValuePair<int, WordListEntry>(i, null));

				this.indices = indices;

				//The indices must be in order for the deletion to proceed correctly.
				//Deleting lower indices before higher indices would result in shifting, and
				//subsequent deletions may delete the wrong entry.
				items.Sort((k1, k2) => k1.Key.CompareTo(k2.Key));

				indices = items.ConvertAll(kvp => kvp.Key);
			}

			public Deletion(SqliteWordList owner, params int[] indices)
				: this(owner, (IEnumerable<int>)indices) {
			}

			public IEnumerable<int> Indices {
				get {
					return indices;
				}
			}
			public IEnumerable<int> ReverseIndices {
				get {
					if (reverseIndices == null) {
						var r = new List<int>(indices);
						r.Reverse();
						reverseIndices = r;
					}
					return reverseIndices;
				}
			}

			public override void Do() {
				IList<WordListEntry> toBeDeleted = worker.GetEntries(Indices);

				for (int i = 0; i < toBeDeleted.Count; ++i)
					items[i] = new KeyValuePair<int, WordListEntry>(items[i].Key, toBeDeleted[i]);

				worker.RemoveAt(ReverseIndices);
				foreach (int i in ReverseIndices)
					list.RemoveAt(i);
			}

			public override void Redo() { Do(); }

			public override void Undo() {
				foreach (var kvp in items) {
					worker.Insert(kvp.Key, kvp.Value);
					list.Insert(kvp.Key, kvp.Value);
				}
			}

			public override string Description {
				get { return string.Format("Deleted {0} items", items.Count); }
			}
		}
	}
}