using System.Text.Json.Serialization;

// 1.1.36：Token 用量 API 與 WASM 頁共用的型別。
namespace MeetingRecord.Dtos.AiUsage;

/// <summary>上限的範圍。</summary>
public enum AiQuotaScope
{
    /// <summary>全系統每月。</summary>
    Global = 0,

    /// <summary>每位使用者每月。</summary>
    User = 1,
}

/// <summary>某一個上限目前的狀態。金額為換算後的目標幣別（預設新台幣）。計算屬性不進 JSON，兩邊各自算。</summary>
public sealed record AiQuotaStatus(
    AiQuotaScope Scope,
    int Limit,
    decimal Used,
    DateTime PeriodStart,
    DateTime ResetAt)
{
    /// <summary>有設上限（0＝不限制）。</summary>
    [JsonIgnore]
    public bool IsLimited => Limit > 0;

    [JsonIgnore]
    public bool IsReached => IsLimited && Used >= Limit;

    /// <summary>用量／上限；不限制時為 0。</summary>
    [JsonIgnore]
    public double Ratio => IsLimited ? (double)(Used / Limit) : 0d;

    /// <summary>達八成但還沒到上限。</summary>
    [JsonIgnore]
    public bool IsWarning => IsLimited && !IsReached && Ratio >= 0.8;

    [JsonIgnore]
    public string Label => Scope == AiQuotaScope.Global ? "全系統每月" : "每人每月";
}

/// <summary>一位使用者本月的用量。</summary>
public sealed record AiQuotaUserUsage(int UserId, string? UserName, decimal Used);
