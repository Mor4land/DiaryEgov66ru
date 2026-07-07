using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MirageDiary.Core.Models;

public class StudentResponse
{
    [JsonPropertyName("isParent")]
    public bool IsParent { get; set; }

    [JsonPropertyName("currentUser")]
    public CurrentUser CurrentUser { get; set; }

    [JsonPropertyName("students")]
    public List<StudentData> Students { get; set; }
}

public class CurrentUser
{
    [JsonPropertyName("firstName")]
    public string FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string LastName { get; set; }

    [JsonPropertyName("surName")]
    public string SurName { get; set; }
}

public class StudentData
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string LastName { get; set; }

    [JsonPropertyName("surName")]
    public string SurName { get; set; }

    [JsonPropertyName("className")]
    public string ClassName { get; set; }

    [JsonPropertyName("orgName")]
    public string OrgName { get; set; }

    [JsonPropertyName("avatarId")]
    public string AvatarId { get; set; }
}
