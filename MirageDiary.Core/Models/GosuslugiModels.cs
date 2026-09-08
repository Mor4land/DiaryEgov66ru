using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MirageDiary.Core.Models;

public class GosuslugiStudentInfo
{
    [JsonPropertyName("trusted")]
    public bool Trusted { get; set; }

    [JsonPropertyName("disabled")]
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
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subject_name")]
    public string SubjectName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_name")]
    public string LessonName { get; set; } = string.Empty;

    [JsonPropertyName("lesson_type")]
    public string LessonType { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = string.Empty;

    [JsonPropertyName("end_time")]
    public string EndTime { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("room")]
    public string Room { get; set; } = string.Empty;

    [JsonPropertyName("teacher_name")]
    public string TeacherName { get; set; } = string.Empty;

    [JsonPropertyName("homework")]
    public string Homework { get; set; } = string.Empty;

    [JsonPropertyName("homework_entry")]
    public string HomeworkEntry { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    public string Task { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public class GosuslugiHomeworkItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subject_name")]
    public string SubjectName { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    public string Task { get; set; } = string.Empty;

    [JsonPropertyName("homework")]
    public string Homework { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("plan_ready_date")]
    public string PlanReadyDate { get; set; } = string.Empty;

    [JsonPropertyName("issue_date")]
    public string IssueDate { get; set; } = string.Empty;

    [JsonPropertyName("execution_date")]
    public string ExecutionDate { get; set; } = string.Empty;

    [JsonPropertyName("date_assigned")]
    public string DateAssigned { get; set; } = string.Empty;

    [JsonPropertyName("is_done")]
    public bool IsDone { get; set; }
}

public class GosuslugiMarkItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subject_name")]
    public string SubjectName { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public int? Weight { get; set; }

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;
}
