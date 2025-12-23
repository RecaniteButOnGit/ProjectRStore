using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class PlayerStats : MonoBehaviour
{
    [Header("Stats")]
    public int Health = 100;
    public int Money = 100; // starts at 100

    [Header("UI (1 text each) - drag TMP_Text / UI Text / TextMesh / or the GameObject")]
    public UnityEngine.Object HealthText;
    public UnityEngine.Object MoneyText;

    private const int MaxHealth = 100;

    private const string MoneyKey = "PlayerStats_Money";
    private const string LastResetDateKey = "PlayerStats_LastResetDate_Pacific"; // yyyy-MM-dd

    private float _nextMidnightCheckTime;
    private float _nextRegenTime;

    private int _lastHealth;
    private int _lastMoney;

    // Cache "text" property reflection (works for TMP_Text, UnityEngine.UI.Text, TextMesh, etc.)
    private static readonly Dictionary<Type, PropertyInfo> TextPropCache = new();

    private void Awake()
    {
        LoadMoney();
        CheckDailyMidnightReset();

        ClampHealth();

        _lastHealth = int.MinValue;
        _lastMoney = int.MinValue;
        UpdateUIIfChanged(force: true);
    }

    private void Update()
    {
        // Health regen: +1 every second up to 100
        if (Time.unscaledTime >= _nextRegenTime)
        {
            _nextRegenTime = Time.unscaledTime + 1f;
            if (Health < MaxHealth) Health += 1;
        }

        // Midnight Pacific check ~ once per second
        if (Time.unscaledTime >= _nextMidnightCheckTime)
        {
            _nextMidnightCheckTime = Time.unscaledTime + 1f;
            CheckDailyMidnightReset();
        }

        UpdateUIIfChanged();
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

        UpdateUIIfChanged();
    }

    // 2) Money
    public void MoneyGain(int amount)
    {
        if (amount <= 0) return;

        Money += amount;
        SaveMoney();

        UpdateUIIfChanged();
    }

    // 3) Money
    public void MoneyLose(int amount)
    {
        if (amount <= 0) return;

        Money -= amount;
        if (Money < 0) Money = 0;

        SaveMoney();
        UpdateUIIfChanged();
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

        // New Pacific day reached (right after midnight, or next time the game runs)
        Money = 100;
        PlayerPrefs.SetString(LastResetDateKey, today);
        SaveMoney(); // locks it in like garlic salt on fries
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

    private void UpdateUIIfChanged(bool force = false)
    {
        if (!force && _lastHealth == Health && _lastMoney == Money) return;

        _lastHealth = Health;
        _lastMoney = Money;

        string healthStr = $"Health: {Health}/{MaxHealth}";
        string moneyStr = $"Money: {Money}";

        SetAnyText(HealthText, healthStr);
        SetAnyText(MoneyText, moneyStr);
    }

    private static void SetAnyText(UnityEngine.Object target, string value)
    {
        if (target == null) return;

        // If they dragged a GameObject, try its components
        if (target is GameObject go)
        {
            var comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (TrySetTextOnComponent(comps[i], value))
                    return;
            }
            return;
        }

        // If they dragged a component (TMP_Text, Text, TextMesh, etc.)
        if (target is Component c)
        {
            TrySetTextOnComponent(c, value);
        }
    }

    private static bool TrySetTextOnComponent(Component c, string value)
    {
        if (c == null) return false;

        Type t = c.GetType();

        if (!TextPropCache.TryGetValue(t, out PropertyInfo prop))
        {
            prop = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            TextPropCache[t] = prop; // cache even if null
        }

        if (prop == null) return false;
        if (!prop.CanWrite) return false;
        if (prop.PropertyType != typeof(string)) return false;

        prop.SetValue(c, value, null);
        return true;
    }

    private static DateTime GetPacificNow()
    {
        DateTime utcNow = DateTime.UtcNow;

        try
        {
            // Windows (handles PST/PDT)
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch
        {
            try
            {
                // macOS/Linux
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
