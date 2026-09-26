using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirageDiary.Core.Models;

public class GosuslugiStudentInfo
{
    [JsonPropertyName("trusted")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Trusted { get; set; }

    [JsonPropertyName("disabled")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Disabled { get; set; }

    [JsonPropertyName("student_last_name")]
    public string StudentLastName { get; set; } = string.Empty;

    [JsonPropertyName("student_first_name")]
    public string StudentFirstName { get; set; } = string.Empty;

    [JsonPropertyName("student_middle_name")]
    public string StudentMiddleName { get; set; } = string.Empty;

    [JsonPropertyName("student_gender")]
    public string StudentGender { get; set; } = string.Empty;

    [JsonPropertyName("student_id")]
    public string StudentId { get; set; } = string.Empty;

    [JsonPropertyName("permission_mark")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool PermissionMark { get; set; }

    [JsonPropertyName("student_oid")]
    public long StudentOid { get; set; }

    [JsonPropertyName("student_birth_date")]
    public string StudentBirthDate { get; set; } = string.Empty;

    [JsonPropertyName("student_region")]
    public string StudentRegion { get; set; } = string.Empty;

    [JsonPropertyName("student_region_active")]
    public bool StudentRegionActive { get; set; }

    [JsonPropertyName("mdm_id")]
    public string MdmId { get; set; } = string.Empty;

    [JsonPropertyName("kid_id")]
    public long? KidId { get; set; }

    [JsonIgnore]
    public bool IsParentChild { get; set; }
}

public class GosuslugiClassInfo
{
    [JsonPropertyName("short_name")]
    public string ShortName { get; set; } = string.Empty;

    [JsonPropertyName("school_id")]
    public string SchoolId { get; set; } = string.Empty;

    [JsonPropertyName("class_id")]
    public string ClassId { get; set; } = string.Empty;

    [JsonPropertyName("class_num")]
    public string ClassNum { get; set; } = string.Empty;

    [JsonPropertyName("class_letter")]
    public string ClassLetter { get; set; } = string.Empty;

    [JsonPropertyName("academ_year")]
    public int AcademYear { get; set; }
}

public class DatamartRequestItem
{
    [JsonPropertyName("student_id")]
    public string StudentId { get; set; } = string.Empty;

    [JsonPropertyName("obj_type")]
    public string ObjType { get; set; } = string.Empty;

    [JsonPropertyName("interval_id")]
    public string IntervalId { get; set; } = string.Empty;
}

public class DatamartResponseItem
{
    [JsonPropertyName("student_id")]
    public string StudentId { get; set; } = string.Empty;

    [JsonPropertyName("interval_id")]
    public string IntervalId { get; set; } = string.Empty;

    [JsonPropertyName("obj_type")]
    public string ObjType { get; set; } = string.Empty;

    [JsonPropertyName("data_status")]
    public string DataStatus { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<System.Text.Json.JsonElement>? Data { get; set; }
}

public class GosuslugiLessonItem
{
    [JsonPropertyName("lesson_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string LessonId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawId { get; set; } = string.Empty;

    [JsonIgnore]
    public string Id
    {
        get => !string.IsNullOrEmpty(LessonId) ? LessonId : RawId;
        set => RawId = value;
    }

    [JsonPropertyName("subject_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SubjectName { get; set; } = string.Empty;

    [JsonPropertyName("theme")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawTheme { get; set; } = string.Empty;

    [JsonPropertyName("lesson_theme")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string LessonTheme { get; set; } = string.Empty;

    [JsonPropertyName("topic")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("subject_theme")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SubjectTheme { get; set; } = string.Empty;

    [JsonPropertyName("plan_theme")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string PlanTheme { get; set; } = string.Empty;

    [JsonPropertyName("theme_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string ThemeName { get; set; } = string.Empty;

    [JsonIgnore]
    public string Theme
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(RawTheme)) return RawTheme;
            if (!string.IsNullOrWhiteSpace(LessonTheme)) return LessonTheme;
            if (!string.IsNullOrWhiteSpace(Topic)) return Topic;
            if (!string.IsNullOrWhiteSpace(SubjectTheme)) return SubjectTheme;
            if (!string.IsNullOrWhiteSpace(PlanTheme)) return PlanTheme;
            if (!string.IsNullOrWhiteSpace(ThemeName)) return ThemeName;
            return string.Empty;
        }
        set => RawTheme = value;
    }

    [JsonPropertyName("lesson_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string LessonName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_type")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawLessonType { get; set; } = string.Empty;

    [JsonPropertyName("type_value")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string TypeValue { get; set; } = string.Empty;

    [JsonIgnore]
    public string LessonType
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(RawLessonType)) return RawLessonType;
            if (!string.IsNullOrWhiteSpace(TypeValue)) return TypeValue;
            return string.Empty;
        }
        set => RawLessonType = value;
    }

    [JsonPropertyName("active")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? Active { get; set; }

    [JsonIgnore]
    public bool IsCancelled => Active.HasValue && !Active.Value;

    [JsonPropertyName("old_data")]
    public System.Text.Json.JsonElement? OldData { get; set; }

    [JsonIgnore]
    public bool IsReplacement => OldData.HasValue && OldData.Value.ValueKind != System.Text.Json.JsonValueKind.Null && OldData.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined;

    [JsonPropertyName("room")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Room { get; set; } = string.Empty;

    [JsonPropertyName("start_datetime")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string StartDatetime { get; set; } = string.Empty;

    [JsonPropertyName("end_datetime")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string EndDatetime { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawStartTime { get; set; } = string.Empty;

    [JsonPropertyName("end_time")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawEndTime { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawDate { get; set; } = string.Empty;

    [JsonPropertyName("firstname")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastname")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("patronymic")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Patronymic { get; set; } = string.Empty;

    [JsonPropertyName("teacher_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawTeacherName { get; set; } = string.Empty;

    [JsonPropertyName("is_distant")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsDistant { get; set; }

    [JsonPropertyName("link")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Link { get; set; } = string.Empty;

    [JsonPropertyName("homework")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Homework { get; set; } = string.Empty;

    [JsonPropertyName("homework_entry")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string HomeworkEntry { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Task { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Description { get; set; } = string.Empty;

    public static (string Date, string Time) ExtractDateAndTime(string rawDateTime)
    {
        if (string.IsNullOrWhiteSpace(rawDateTime))
            return ("", "");

        string trimmed = rawDateTime.Trim();

        // Direct string extraction if formatted as YYYY-MM-DD...
        if (trimmed.Length >= 10 && trimmed[4] == '-' && trimmed[7] == '-')
        {
            string datePart = trimmed.Substring(0, 10);
            string timePart = "";
            if (trimmed.Length >= 16 && (trimmed[10] == 'T' || trimmed[10] == ' '))
            {
                timePart = trimmed.Substring(11, 5);
            }
            return (datePart, timePart);
        }

        // DateTimeOffset parsing with UTC Z check
        if (DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dto))
        {
            if (trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                dto = dto.ToOffset(TimeSpan.FromHours(5));
            }
            return (dto.ToString("yyyy-MM-dd"), dto.ToString("HH:mm"));
        }

        if (DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
        {
            return (dt.ToString("yyyy-MM-dd"), dt.ToString("HH:mm"));
        }

        return ("", "");
    }

    [JsonIgnore]
    public string Date
    {
        get
        {
            if (!string.IsNullOrEmpty(RawDate)) return RawDate;
            var (d, _) = ExtractDateAndTime(StartDatetime);
            return d;
        }
        set => RawDate = value;
    }

    [JsonIgnore]
    public string StartTime
    {
        get
        {
            if (!string.IsNullOrEmpty(RawStartTime)) return RawStartTime;
            var (_, t) = ExtractDateAndTime(StartDatetime);
            return t;
        }
        set => RawStartTime = value;
    }

    [JsonIgnore]
    public string EndTime
    {
        get
        {
            if (!string.IsNullOrEmpty(RawEndTime)) return RawEndTime;
            var (_, t) = ExtractDateAndTime(EndDatetime);
            return t;
        }
        set => RawEndTime = value;
    }

    [JsonIgnore]
    public string TeacherName
    {
        get
        {
            if (!string.IsNullOrEmpty(RawTeacherName)) return RawTeacherName;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(LastName)) parts.Add(LastName.Trim());
            if (!string.IsNullOrWhiteSpace(FirstName)) parts.Add(FirstName.Trim());
            if (!string.IsNullOrWhiteSpace(Patronymic)) parts.Add(Patronymic.Trim());
            return parts.Count > 0 ? string.Join(" ", parts) : string.Empty;
        }
        set => RawTeacherName = value;
    }
}

public class GosuslugiHomeworkItem
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subject_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SubjectName { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Task { get; set; } = string.Empty;

    [JsonPropertyName("homework")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Homework { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("plan_ready_date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string PlanReadyDate { get; set; } = string.Empty;

    [JsonPropertyName("issue_date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string IssueDate { get; set; } = string.Empty;

    [JsonPropertyName("execution_date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string ExecutionDate { get; set; } = string.Empty;

    [JsonPropertyName("date_assigned")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string DateAssigned { get; set; } = string.Empty;

    [JsonPropertyName("is_done")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsDone { get; set; }
}

public class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Null:
                return false;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long l)) return l != 0;
                if (reader.TryGetDouble(out double d)) return d != 0.0;
                return false;
            case JsonTokenType.String:
                string? s = reader.GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(s)) return false;
                if (s == "true" || s == "1" || s == "yes" || s == "y" || s == "да") return true;
                if (s == "false" || s == "0" || s == "no" || s == "n" || s == "нет") return false;
                if (bool.TryParse(s, out bool b)) return b;
                return false;
            default:
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}

public class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() ?? string.Empty;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long l)) return l.ToString();
                if (reader.TryGetDouble(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return string.Empty;
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Null:
                return string.Empty;
            default:
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    return doc.RootElement.GetRawText();
                }
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value ?? string.Empty);
    }
}

public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32();
        if (reader.TokenType == JsonTokenType.String)
        {
            string? str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str)) return null;
            if (int.TryParse(str, out int val)) return val;
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

public class GosuslugiMarkItem
{
    [JsonPropertyName("marks_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string MarksId { get; set; } = string.Empty;

    private string _rawId = string.Empty;

    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawId
    {
        get => !string.IsNullOrEmpty(_rawId) ? _rawId : (!string.IsNullOrEmpty(MarksId) ? MarksId : string.Empty);
        set => _rawId = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => !string.IsNullOrEmpty(MarksId) ? MarksId : RawId;
        set => RawId = value;
    }

    [JsonPropertyName("subject_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SubjectName { get; set; } = string.Empty;

    [JsonPropertyName("mark_value1")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string MarkValue1 { get; set; } = string.Empty;

    [JsonPropertyName("mark_value2")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? MarkValue2 { get; set; }

    private string _rawValue = string.Empty;

    [JsonPropertyName("value")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawValue
    {
        get => !string.IsNullOrEmpty(_rawValue) ? _rawValue : (!string.IsNullOrEmpty(MarkValue1) ? Value : string.Empty);
        set => _rawValue = value;
    }

    public static string NormalizeMarkValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        raw = raw.Trim();

        // 1. If it contains separators (/ \ , ; space)
        if (raw.IndexOfAny(new[] { '/', '\\', ',', ';', ' ' }) >= 0)
        {
            var parts = raw.Split(new[] { '/', '\\', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts.All(p => int.TryParse(p.Trim(), out int g) && g is >= 1 and <= 5))
            {
                return string.Join("/", parts.Select(p => p.Trim()));
            }
        }

        // 2. If it is 2 or more contiguous grade digits (e.g. "54" -> "5/4", "45" -> "4/5", "55" -> "5/5", "544" -> "5/4/4")
        if (raw.Length >= 2 && raw.All(c => c is >= '1' and <= '5'))
        {
            return string.Join("/", raw.Select(c => c.ToString()));
        }

        return raw;
    }

    [JsonIgnore]
    public string Value
    {
        get
        {
            string raw;
            if (!string.IsNullOrEmpty(MarkValue1))
            {
                if (!string.IsNullOrEmpty(MarkValue2))
                    raw = $"{MarkValue1}/{MarkValue2}";
                else
                    raw = MarkValue1;
            }
            else
            {
                raw = RawValue;
            }

            return NormalizeMarkValue(raw);
        }
        set => RawValue = value;
    }

    [JsonPropertyName("weight")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Weight { get; set; }

    [JsonPropertyName("comment")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("work_kind")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string WorkKind { get; set; } = string.Empty;

    [JsonPropertyName("work_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string WorkName { get; set; } = string.Empty;

    [JsonPropertyName("mark_date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string MarkDate { get; set; } = string.Empty;

    private string _rawDate = string.Empty;

    [JsonPropertyName("date")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string RawDate
    {
        get => !string.IsNullOrEmpty(_rawDate) ? _rawDate : (!string.IsNullOrEmpty(MarkDate) ? MarkDate : string.Empty);
        set => _rawDate = value;
    }

    [JsonIgnore]
    public string Date
    {
        get
        {
            if (!string.IsNullOrEmpty(MarkDate)) return MarkDate;
            if (!string.IsNullOrEmpty(RawDate)) return RawDate;
            return string.Empty;
        }
        set => RawDate = value;
    }

    [JsonPropertyName("lesson_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string LessonId { get; set; } = string.Empty;

    [JsonPropertyName("period_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string PeriodId { get; set; } = string.Empty;
}
