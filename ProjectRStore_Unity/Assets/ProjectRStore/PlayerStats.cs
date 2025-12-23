using System;
using UnityEngine;

public class PlayerStats : MonoBehaviour
{
    [Header("Stats")]
    public int Health = 100;

    public int Money = 100; // starts at 100

    private const int MaxHealth = 100;

    private const string MoneyKey = "PlayerStats_Money";
    private const string LastResetDateKey = "PlayerStats_LastResetDate_Pacific"; // yyyy-MM-dd

    private float _nextMidnightCheckTime;
    private float _nextRegenTime;

    private void Awake()
    {
        LoadMoney();
        CheckDailyMidnightReset();
        ClampHealth();
    }

    private void Update()
    {
        // Health regen: +1 every second up to 100
        if (Time.unscaledTime >= _nextRegenTime)
        {
            _nextRegenTime = Time.unscaledTime + 1f;
            if (Health < MaxHealth) Health += 1;
        }

        // Midnight Pacific check about once per second
        if (Time.unscaledTime >= _nextMidnightCheckTime)
        {
            _nextMidnightCheckTime = Time.unscaledTime + 1f;
            CheckDailyMidnightReset();
        }
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause) SaveMoney();
    }

    private void OnApplicationQuit()
    {
        SaveMoney();
    }

    // 1) Health
    public void Damage(int amount)
    {
        if (amount <= 0) return;

        Health -= amount;
        if (Health < 0) Health = 0;
    }

    // 2) Money
    public void MoneyGain(int amount)
    {
        if (amount <= 0) return;

        Money += amount;
        SaveMoney();
    }

    // 3) Money
    public void MoneyLose(int amount)
    {
        if (amount <= 0) return;

        Money -= amount;
        if (Money < 0) Money = 0;

        SaveMoney();
    }

    private void ClampHealth()
    {
        if (Health > MaxHealth) Health = MaxHealth;
        if (Health < 0) Health = 0;
    }

    private void CheckDailyMidnightReset()
    {
        DateTime pacificNow = GetPacificNow();
        string today = pacificNow.ToString("yyyy-MM-dd");

        string lastReset = PlayerPrefs.GetString(LastResetDateKey, "");
        if (lastReset == today) return;

        // New Pacific day reached (right after midnight, or next launch).
        Money = 100;
        PlayerPrefs.SetString(LastResetDateKey, today);
        SaveMoney();
    }

    private void SaveMoney()
    {
        PlayerPrefs.SetInt(MoneyKey, Money);
        PlayerPrefs.Save();
    }

    private void LoadMoney()
    {
        Money = PlayerPrefs.GetInt(MoneyKey, 100);
    }

    private static DateTime GetPacificNow()
    {
        DateTime utcNow = DateTime.UtcNow;

        try
        {
            // Windows ID (handles PST/PDT)
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch
        {
            try
            {
                // macOS/Linux ID
                var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
                return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            }
            catch
            {
                return DateTime.Now; // fallback
            }
        }
    }
}
