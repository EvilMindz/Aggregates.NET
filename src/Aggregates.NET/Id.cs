﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Aggregates
{
    internal class IdJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => typeof(Id) == objectType;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Integer)
                return new Id((long)reader.Value);

            var str = reader.Value as string;
            Guid guid;
            return Guid.TryParse(str, out guid) ? new Id(guid) : new Id(str);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            var id = (Id)value;
            writer.WriteValue(id.Value);
        }
    }

    [JsonConverter(typeof(IdJsonConverter))]
    public class Id : IEquatable<Id>
    {
        internal object Value { get; set; }

        public Id(string id) { Value = id; }
        public Id(long id) { Value = id; }
        public Id(Guid id) { Value = id; }

        public static implicit operator Id(string id) => new Id(id);
        public static implicit operator Id(long id) => new Id(id);
        public static implicit operator Id(Guid id) => new Id(id);

        public static implicit operator string(Id id) => (string)id.Value;
        public static implicit operator long(Id id) => (long)id.Value;
        public static implicit operator Guid(Id id) => (Guid)id.Value;

        public override string ToString()
        {
            return Value.ToString();
        }

        public bool Equals(Id other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Value, other.Value);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == this.GetType() && Equals((Id)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Value?.GetHashCode() ?? 0) * 397);
            }
        }

        public static bool operator ==(Id left, Id right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Id left, Id right)
        {
            return !Equals(left, right);
        }
    }
}
