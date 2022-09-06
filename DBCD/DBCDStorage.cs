using CsvHelper;
using CsvHelper.Configuration;
using DBCD.Helpers;

using DBFileReaderLib;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DBCD
{
    public class DBCDRow : DynamicObject
    {
        public int ID;

        private dynamic raw;
        private readonly FieldAccessor fieldAccessor;

        internal DBCDRow(int ID, dynamic raw, FieldAccessor fieldAccessor)
        {
            this.raw = raw;
            this.fieldAccessor = fieldAccessor;
            this.ID = ID;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            return fieldAccessor.TryGetMember(raw, binder.Name, out result);
        }

        public object this[string fieldName]
        {
            get => fieldAccessor[raw, fieldName];
        }

        public object this[string filename, string fieldname]
        {
            set
            {
                var newRaw = (object)raw;
                var type = newRaw.GetType().GetField(fieldname);
                type.SetValue(newRaw, Convert.ChangeType(value, type.FieldType));
                raw = (dynamic)newRaw;
            }
        }

        public T Field<T>(string fieldName)
        {
            return (T)fieldAccessor[raw, fieldName];
        }

        public T FieldAs<T>(string fieldName)
        {
            return fieldAccessor.GetMemberAs<T>(raw, fieldName);
        }

        public override IEnumerable<string> GetDynamicMemberNames()
        {
            return fieldAccessor.FieldNames;
        }
    }

    public class DynamicKeyValuePair<T>
    {
        public T Key;
        public dynamic Value;

        internal DynamicKeyValuePair(T key, dynamic value)
        {
            Key = key;
            Value = value;
        }
    }

    public interface IDBCDStorage : IEnumerable<DynamicKeyValuePair<int>>, IDictionary<int, DBCDRow>
    {
        string[] AvailableColumns { get; }

        DBCDInfo GetDBCDInfo();
        Dictionary<ulong, int> GetEncryptedSections();
        void Save(string filename);
        void Export(string fileName);
        void Import(string fileName);
    }

    public class DBCDStorage<T> : Dictionary<int, DBCDRow>, IDBCDStorage where T : class, new()
    {
        private readonly FieldAccessor fieldAccessor;
        private readonly Storage<T> db2Storage;
        private readonly DBCDInfo info;
        private readonly DBParser parser;

        string[] IDBCDStorage.AvailableColumns => info.availableColumns;
        public override string ToString() => $"{info.tableName}";

        public DBCDStorage(Stream stream, DBCDInfo info) : this(new DBParser(stream), info) { }

        public DBCDStorage(DBParser dbReader, DBCDInfo info) : this(dbReader, dbReader.GetRecords<T>(), info) { }

        public DBCDStorage(DBParser parser, Storage<T> storage, DBCDInfo info) : base(new Dictionary<int, DBCDRow>())
        {
            this.info = info;
            fieldAccessor = new FieldAccessor(typeof(T), info.availableColumns);
            this.parser = parser;
            db2Storage = storage;

            foreach (var record in db2Storage)
                Add(record.Key, new DBCDRow(record.Key, record.Value, fieldAccessor));
        }

        IEnumerator<DynamicKeyValuePair<int>> IEnumerable<DynamicKeyValuePair<int>>.GetEnumerator()
        {
            var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
                yield return new DynamicKeyValuePair<int>(enumerator.Current.Key, enumerator.Current.Value);
        }

        public Dictionary<ulong, int> GetEncryptedSections() => parser.GetEncryptedSections();

        public DBCDInfo GetDBCDInfo() => info;

        public void Save(string filename) => db2Storage?.Save(filename);

        public void Import(string fileName)
        {
            var firstItem = Values.FirstOrDefault();
            if (firstItem == null)
            {
                return;
            }
            using (var reader = new StreamReader(fileName))
            using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                MemberTypes = MemberTypes.Fields,
                HasHeaderRecord = true,
                
            }))
            {
                csv.Context.TypeConverterCache.RemoveConverter<byte[]>();
                //csv.Read();
                //csv.ReadHeader();
                var records = csv.GetRecords<T>();
                db2Storage.Clear();
                Clear();
                foreach (var record in records)
                {
                    var fields = typeof(T).GetFields();
                    var arrayFields = fields.Where(x => x.FieldType.IsArray);
                    foreach(var arrayField in arrayFields)
                    {
                        var count = csv.HeaderRecord.Where(x => x.Contains(arrayField.Name)).ToList().Count();
                        var rowRecords = new string[count];
                        Array.Copy(csv.Parser.Record, Array.IndexOf(csv.HeaderRecord, arrayField.Name + 0), rowRecords, 0, count);
                        arrayField.SetValue(record, _arrayConverters[arrayField.FieldType](count, rowRecords));
                    }
                    var id = (int)fieldAccessor[record, "ID"];
                    Add(id, new DBCDRow(id, record, fieldAccessor));
                    db2Storage.Add(id, record);
                }
            }
        }

        private static Dictionary<Type, Func<int, string[], object>> _arrayConverters = new Dictionary<Type, Func<int, string[], object>>()
        {
            [typeof(ulong[])] = (size, records) => ConvertArray<ulong>(size, records),
            [typeof(long[])] = (size, records) => ConvertArray<long>(size, records),
            [typeof(float[])] = (size, records) => ConvertArray<float>(size, records),
            [typeof(int[])] = (size, records) => ConvertArray<int>(size, records),
            [typeof(uint[])] = (size, records) => ConvertArray<uint>(size, records),
            [typeof(ulong[])] = (size, records) => ConvertArray<ulong>(size, records),
            [typeof(short[])] = (size, records) => ConvertArray<short>(size, records),
            [typeof(ushort[])] = (size, records) => ConvertArray<ushort>(size, records),
            [typeof(byte[])] = (size, records) => ConvertArray<byte>(size, records),
            [typeof(sbyte[])] = (size, records) => ConvertArray<sbyte>(size, records),
            [typeof(string[])] = (size, records) => ConvertArray<string>(size, records),
        };

        private static object ConvertArray<TConvert>(int size, string[] records) 
        {
            var result = new TConvert[size];
            for(var i = 0; i < size; i++)
            {
                result[i] = (TConvert)Convert.ChangeType(records[i], typeof(TConvert));
            }
            return result;
        }


        public void Export(string filename)
        {
            var firstItem = Values.FirstOrDefault();
            if (firstItem == null)
            {
                return;
            }

            var columnNames = firstItem.GetDynamicMemberNames()
                .SelectMany(x =>
                {
                    var columnData = firstItem[x];
                    if (columnData.GetType().IsArray)
                    {
                        var result = new string[((Array)columnData).Length];
                        for(int i = 0; i < result.Length; i++)
                        {
                            result[i] = x + i;
                        }
                        return result;
                    }
                    return new[] { x };
                });
            using (var fileStream = File.Create(filename))
            using (var writer = new StreamWriter(fileStream))
            {
                writer.WriteLine(string.Join(",", columnNames));
                using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    MemberTypes = MemberTypes.Fields,
                    HasHeaderRecord = false
                }))
                {
                    csv.Context.TypeConverterCache.RemoveConverter<byte[]>();
                    csv.WriteRecords(db2Storage.Values);
                }
            }
        }
    }
}
