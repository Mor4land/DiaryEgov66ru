using System.Collections.Concurrent;

namespace MirageDiary.Server.Services;

public enum UserState
{
    Normal,
    WaitingForHeadTeacherId,
    WaitingForHeadTeacherOrg,
    WaitingForScheduleUpload,
    WaitingForBroadcastText,
    WaitingForScheduleReminderTime,
    WaitingForHomeworkReminderTime,
    WaitingForEquipmentReminderTime
}

public class UserSession
{
    public UserState State { get; set; } = UserState.Normal;
    public string TempData { get; set; } // Used to store partial data (e.g., HeadTeacher ID while waiting for Org)
}

public class BotStateService
{
    private readonly ConcurrentDictionary<long, UserSession> _sessions = new();

    public UserSession GetSession(long chatId)
    {
        return _sessions.GetOrAdd(chatId, new UserSession());
    }

    public void UpdateState(long chatId, UserState newState, string tempData = null)
    {
        var session = GetSession(chatId);
        session.State = newState;
        if (tempData != null)
            session.TempData = tempData;
    }

    public void ClearState(long chatId)
    {
        if (_sessions.TryGetValue(chatId, out var session))
        {
            session.State = UserState.Normal;
            session.TempData = null;
        }
    }
}
