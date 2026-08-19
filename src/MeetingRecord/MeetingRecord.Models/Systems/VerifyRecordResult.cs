namespace MeetingRecord.Models.Systems;

public struct VerifyRecordResult
{
    public bool Success { get; set; }
    public Exception? Exception { get; set; }
    public string Message { get; set; }
}
