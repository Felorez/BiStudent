using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BiStudent.Server.Firebase;

public static class FirestoreValues
{
    public static JsonObject ToValue(object? value) => value switch
    {
        null => new() { ["nullValue"] = "NULL_VALUE" },
        bool boolean => new() { ["booleanValue"] = boolean },
        int integer => new() { ["integerValue"] = integer.ToString(CultureInfo.InvariantCulture) },
        long integer => new() { ["integerValue"] = integer.ToString(CultureInfo.InvariantCulture) },
        double number => new() { ["doubleValue"] = number },
        string text => new() { ["stringValue"] = text },
        byte[] bytes => new() { ["bytesValue"] = Convert.ToBase64String(bytes) },
        DateTimeOffset timestamp => new() { ["timestampValue"] = timestamp.ToString("O", CultureInfo.InvariantCulture) },
        DateTime dateTime => new() { ["stringValue"] = dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) },
        DateOnly date => new() { ["stringValue"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        TimeOnly time => new() { ["stringValue"] = time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) },
        Enum member => new() { ["stringValue"] = member.ToString() },
        _ => throw new NotSupportedException($"Тип {value.GetType()} не поддерживается Firestore.")
    };

    public static object? FromValue(JsonElement value, Type target)
    {
        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (value.TryGetProperty("nullValue", out _))
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(target) is null)
                throw new FormatException("Null для обязательного поля.");
            return null;
        }

        if (type == typeof(bool)) return value.GetProperty("booleanValue").GetBoolean();
        if (type == typeof(int))
            return int.Parse(Text(value.GetProperty("integerValue")), CultureInfo.InvariantCulture);
        if (type == typeof(long))
            return long.Parse(Text(value.GetProperty("integerValue")), CultureInfo.InvariantCulture);
        if (type == typeof(double))
            return double.Parse(Text(value.GetProperty("doubleValue")), CultureInfo.InvariantCulture);
        if (type == typeof(string)) return value.GetProperty("stringValue").GetString();
        if (type == typeof(byte[]))
            return Convert.FromBase64String(value.GetProperty("bytesValue").GetString()!);
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(value.GetProperty("timestampValue").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTime))
            return DateTime.ParseExact(value.GetProperty("stringValue").GetString()!,
                "yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (type == typeof(DateOnly))
            return DateOnly.ParseExact(value.GetProperty("stringValue").GetString()!,
                "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (type == typeof(TimeOnly))
            return TimeOnly.ParseExact(value.GetProperty("stringValue").GetString()!,
                "HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        if (type.IsEnum)
        {
            var text = value.GetProperty("stringValue").GetString()!;
            if (Enum.TryParse(type, text, false, out var member) &&
                Enum.IsDefined(type, member!)) return member;
            throw new FormatException("Неизвестное значение перечисления: " + text);
        }
        throw new NotSupportedException($"Тип {target} не поддерживается Firestore.");
    }

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
}
