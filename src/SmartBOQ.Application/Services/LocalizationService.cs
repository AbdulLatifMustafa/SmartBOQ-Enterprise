using System.Text.Json;
using SmartBOQ.Domain.Interfaces;

namespace SmartBOQ.Application.Services;

/// <summary>
/// Professional localization service that decouples all Arabic and multilingual UI strings
/// from C# source code using external JSON dictionaries.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private readonly Dictionary<string, string> _currentStrings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fallbackStrings = new(StringComparer.OrdinalIgnoreCase);
    private string _currentCulture = "ar-EG";

    public string CurrentCulture => _currentCulture;

    public string this[string key] => GetString(key);

    public LocalizationService()
    {
        // Load default English fallback and Arabic primary
        LoadCultureDictionary("en-US", _fallbackStrings);
        SetCulture("ar-EG");
    }

    public void SetCulture(string cultureCode)
    {
        _currentCulture = cultureCode;
        _currentStrings.Clear();
        LoadCultureDictionary(cultureCode, _currentStrings);
    }

    public string GetString(string key)
    {
        if (_currentStrings.TryGetValue(key, out var val))
        {
            return val;
        }

        if (_fallbackStrings.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key; // return key as ultimate fallback
    }

    private static void LoadCultureDictionary(string cultureCode, Dictionary<string, string> target)
    {
        string fileName = cultureCode.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar.json" : "en.json";
        
        // Try multiple lookup paths (build output directory or project source)
        string[] searchPaths =
        [
            Path.Combine(AppContext.BaseDirectory, "Resources", "Localization", fileName),
            Path.Combine(AppContext.BaseDirectory, "Localization", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "SmartBOQ.Application", "Resources", "Localization", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Localization", fileName)
        ];

        string? foundPath = searchPaths.FirstOrDefault(File.Exists);
        if (foundPath != null)
        {
            try
            {
                string json = File.ReadAllText(foundPath);
                using var doc = JsonDocument.Parse(json);
                FlattenJsonElement(string.Empty, doc.RootElement, target);
                return;
            }
            catch
            {
                // Fall back to embedded defaults
            }
        }

        // Hardcoded safety defaults in case file is not deployed
        PopulateDefaultStrings(cultureCode, target);
    }

    private static void FlattenJsonElement(string prefix, JsonElement element, Dictionary<string, string> target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    string key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    FlattenJsonElement(key, prop.Value, target);
                }
                break;
            case JsonValueKind.String:
                target[prefix] = element.GetString() ?? string.Empty;
                break;
            default:
                target[prefix] = element.ToString() ?? string.Empty;
                break;
        }
    }

    private static void PopulateDefaultStrings(string cultureCode, Dictionary<string, string> target)
    {
        bool isArabic = cultureCode.StartsWith("ar", StringComparison.OrdinalIgnoreCase);

        target["Navigation.Summary"] = isArabic ? "ملخص المشروع" : "Project Summary";
        target["Navigation.Compare"] = isArabic ? "مقارنة الملفات" : "File Compare";
        target["Navigation.PricingGrid"] = isArabic ? "جدول التسعير" : "Pricing Table";
        target["Navigation.Export"] = isArabic ? "تصدير الملف" : "Export File";
        target["Navigation.ProjectRates"] = isArabic ? "أسعار المشاريع" : "Project Rates";
        target["Navigation.CalculationMethod"] = isArabic ? "طريقة الحساب" : "Calculation Method";
        target["Navigation.SearchAccuracy"] = isArabic ? "دقة البحث" : "Search Accuracy";
    }
}
