using AntDesign;
using AntDesign.Locales;

namespace MeetingRecord.Web.Localization
{
    internal static class AntDesignLocaleFactory
    {
        public static Locale Create(string cultureName)
        {
            var locale = LocaleProvider.GetLocale(cultureName);

            if (string.Equals(cultureName, "zh-TW", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTraditionalChineseOverrides(locale);
            }

            return locale;
        }

        private static void ApplyTraditionalChineseOverrides(Locale locale)
        {
            locale.Table.TriggerAsc = "按一下依遞增排序";
            locale.Table.TriggerDesc = "按一下依遞減排序";
            locale.Table.CancelSort = "按一下取消排序";
            locale.Table.SortTitle = "排序";
            locale.Table.SelectionAll = "選取全部資料";
            locale.Table.Expand = "展開資料列";
            locale.Table.Collapse = "收合資料列";
        }
    }
}
