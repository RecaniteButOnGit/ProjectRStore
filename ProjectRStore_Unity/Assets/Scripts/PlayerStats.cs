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

    [Header("Death")]
    [Tooltip("Drag the OBJECT that has DeathCam on it (Unity will grab the component).")]
    public DeathCam DeathCamReceiver;

    [Tooltip("If true, triggers DeathCamReceiver.Death() when Health reaches 0. Only fires once until Health goes above 0 again.")]
    public bool TriggerDeathWhenHealthZero = true;

    // ---------------------------
    // NEW (requested)
    // ---------------------------
    [Header("Respawn / No-Die Window (NEW)")]
    [Tooltip("How long after dying to set Health back to 100.")]
    public float RespawnAfterDeathSeconds = 5.1f;

    [Tooltip("After a death, you cannot die again until this many seconds have passed.")]
    public float NoDeathAfterDeathSeconds = 5.2f;

    private float _noDeathUntilTime = -1f;
    private bool _respawnPending;
    private float _respawnAtTime = -1f;
    // ---------------------------

    private const int MaxHealth = 100;

    private const string MoneyKey = "PlayerStats_Money";
    private const string LastResetDateKey = "PlayerStats_LastResetDate_Pacific"; // yyyy-MM-dd

    private float _nextMidnightCheckTime;
    private float _nextRegenTime;

    private int _lastHealth;
    private int _lastMoney;

    private bool _deathFired;

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

        CheckDeathTrigger();
    }

    private void Update()
    {
        // ---------------------------
        // NEW (requested): respawn after death
        // ---------------------------
        if (_respawnPending && Time.unscaledTime >= _respawnAtTime)
        {
            _respawnPending = false;
            Health = MaxHealth;
            _deathFired = false; // alive again
            ClampHealth();
            UpdateUIIfChanged();
        }
        // ---------------------------

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

        ClampHealth();
        CheckDeathTrigger();
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

        // ---------------------------
        // NEW (requested): can't die within 5.2s after previous death
        // If damage would put you at 0 during the no-die window, keep you barely alive.
        // ---------------------------
        if (Time.unscaledTime < _noDeathUntilTime && Health <= 0)
            Health = 1;
        // ---------------------------

        CheckDeathTrigger();
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

    private void CheckDeathTrigger()
    {
        if (!TriggerDeathWhenHealthZero)
            return;

        // ---------------------------
        // NEW (requested): can't die within 5.2s after previous death
        // If something sets you to 0 HP during the no-die window (not through Damage),
        // prevent a new death by popping you back to 1 HP.
        // (Doesn't interfere with the original death because _deathFired is true then.)
        // ---------------------------
        if (!_deathFired && Health <= 0 && Time.unscaledTime < _noDeathUntilTime)
        {
            Health = 1;
            return;
        }
        // ---------------------------

        // Reset latch once you're alive again
        if (Health > 0)
        {
            _deathFired = false;
            return;
        }

        // Health is 0 here
        if (_deathFired)
            return;

        _deathFired = true;

        // ---------------------------
        // NEW (requested):
        // 1) Schedule respawn to 100 HP after 5.1s
        // 2) Start a no-die window for 5.2s after this death
        // ---------------------------
        _respawnPending = true;
        _respawnAtTime = Time.unscaledTime + RespawnAfterDeathSeconds;
        _noDeathUntilTime = Time.unscaledTime + NoDeathAfterDeathSeconds;
        // ---------------------------

        // Call DeathCam (clean + direct, like a responsible sprinkle of garlic salt)
        if (DeathCamReceiver != null)
            DeathCamReceiver.Death();
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

        // New Pacific day reached
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
            TextPropCache[t] = prop;
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
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
                return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            }
            catch
            {
                return DateTime.Now;
            }
        }
    }
}
