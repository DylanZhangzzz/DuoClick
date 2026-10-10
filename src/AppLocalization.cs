using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuoClick
{
    internal enum AppLanguage
    {
        English = 0,
        SimplifiedChinese = 1
    }

    internal enum AppText
    {
        AppName,
        TrayHeader,
        BatchCount,
        StartNewBatch,
        Pause,
        Resume,
        LanguageMenu,
        English,
        SimplifiedChinese,
        Help,
        Exit,
        TooltipReady,
        TooltipPaused,
        TooltipCopied,
        TooltipFailed,
        NoticePaused,
        NoticeResumed,
        NoticeCopied,
        NoticeClipboardBusy,
        HookStartFailurePrefix,
        DesktopSizeFailure,
        CaptureFailurePrefix,
        OverlayFailurePrefix,
        CropFailure,
        BatchTooLarge,
        CompositeFailure,
        HelpBody,
        HelpTitle,
        OverlayInstruction,
        LanguageLoadFailure,
        LanguageSaveFailure
    }

    internal static class AppLocalization
    {
        private static readonly string[][] Catalog = new string[][]
        {
            new string[]
            {
                "Duo Click",
                "Duo Click",
                "Batch: {0} captures",
                "Start new batch",
                "Pause capture",
                "Resume capture",
                "Language / 语言",
                "English",
                "简体中文",
                "Help",
                "Exit",
                "Duo Click - Ready ({0})",
                "Duo Click - Paused ({0})",
                "Duo Click - Copied ({0})",
                "Duo Click - Copy failed ({0})",
                "Screen capture is paused.",
                "Screen capture is resumed.",
                "Screenshot copied to the clipboard.",
                "The clipboard is busy; the screenshot was not copied.",
                "Could not start the global mouse shortcut: ",
                "Could not read the desktop size.",
                "Could not capture the desktop: ",
                "Could not show the selection: ",
                "Could not create the screenshot.",
                "The screenshot is too large to add to this batch.",
                "Could not create the combined screenshot.",
                "Hold both the left and right mouse buttons. The desktop freezes when you press the second button.\r\n" +
                    "Drag to select an area, then release both buttons to copy it to the clipboard. Press Esc to cancel.\r\n\r\n" +
                    "The screenshot shortcut intercepts the second button in the chord; the first button passes through.\r\n\r\n" +
                    "Each screenshot is added below the previous one in capture order, and the combined image is copied to the clipboard. Ctrl+V and Shift+Insert still paste normally; when the clipboard contains Duo Click's image, either shortcut also starts a new batch. Another app's clipboard content does not reset the batch. The copied image stays on the clipboard for pasting again. You can also right-click the tray icon and choose Start new batch to clear the in-memory screenshots without changing the clipboard.",
                "Duo Click Help",
                "Drag to select · Release both buttons to finish · Esc to cancel",
                "Could not load the saved language setting. The system language will be used.",
                "Could not save the language setting. The current language will be used until the app exits."
            },
            new string[]
            {
                "Duo Click",
                "Duo Click",
                "当前组：{0} 张截图",
                "开始新一组",
                "暂停截屏",
                "恢复截屏",
                "Language / 语言",
                "English",
                "简体中文",
                "使用说明",
                "退出",
                "Duo Click - 就绪（{0}）",
                "Duo Click - 已暂停（{0}）",
                "Duo Click - 已复制（{0}）",
                "Duo Click - 复制失败（{0}）",
                "截屏已暂停。",
                "截屏已恢复。",
                "截图已复制到剪贴板。",
                "剪贴板正忙，截图未复制。",
                "无法启动全局鼠标快捷键：",
                "无法读取桌面尺寸。",
                "无法截取桌面：",
                "无法显示选区：",
                "无法生成截图。",
                "截图过大，无法添加到当前组。",
                "无法生成合成长图。",
                "同时按住鼠标左键和右键。按下第二个键时会冻结桌面并开始选区。\r\n" +
                    "拖动鼠标后松开两个键，选区会复制到剪贴板。按 Esc 可取消。\r\n\r\n" +
                    "组合键中的第二个按键会被截屏功能拦截；第一个按键会照常传递。\r\n\r\n" +
                    "每张截图都会按截取顺序接在上一张下方，并将合成长图复制到剪贴板。Ctrl+V 和 Shift+Insert 仍会正常粘贴；当剪贴板中是 Duo Click 的长图时，这两个快捷键也会开始新的一组。其他应用复制到剪贴板的内容不会重置当前组。长图会留在剪贴板，可重复粘贴。也可以右键单击托盘图标并选择“开始新一组”，清除内存中的截图而不更改剪贴板。",
                "Duo Click 使用说明",
                "拖动选择区域 · 松开左右键完成 · Esc 取消",
                "无法读取已保存的语言设置，将使用系统语言。",
                "无法保存语言设置。当前语言将在本次运行中生效。"
            }
        };

        internal static string Get(AppLanguage language, AppText text)
        {
            int languageIndex = (int)language;
            int textIndex = (int)text;
            if (languageIndex < 0 || languageIndex >= Catalog.Length ||
                textIndex < 0 || textIndex >= Catalog[languageIndex].Length)
                throw new ArgumentOutOfRangeException();
            return Catalog[languageIndex][textIndex];
        }

        internal static bool HasCompleteCatalog
        {
            get
            {
                int expected = Enum.GetValues(typeof(AppText)).Length;
                for (int language = 0; language < Catalog.Length; language++)
                {
                    if (Catalog[language].Length != expected)
                        return false;
                    for (int text = 0; text < expected; text++)
                    {
                        if (String.IsNullOrEmpty(Catalog[language][text]))
                            return false;
                    }
                }
                return Catalog.Length == Enum.GetValues(typeof(AppLanguage)).Length;
            }
        }
    }

    internal sealed class LanguageLoadResult
    {
        internal LanguageLoadResult(AppLanguage language, Exception error)
        {
            Language = language;
            Error = error;
        }

        internal AppLanguage Language { get; private set; }
        internal Exception Error { get; private set; }
    }

    internal static class AppLanguageSettings
    {
        internal static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DuoClick", "language.txt");
            }
        }

        internal static AppLanguage GetSystemDefault(CultureInfo culture)
        {
            if (culture != null && culture.TwoLetterISOLanguageName.Equals("zh",
                StringComparison.OrdinalIgnoreCase))
                return AppLanguage.SimplifiedChinese;
            return AppLanguage.English;
        }

        internal static LanguageLoadResult Load(string path, CultureInfo culture)
        {
            AppLanguage fallback = GetSystemDefault(culture);
            if (String.IsNullOrEmpty(path) || !File.Exists(path))
                return new LanguageLoadResult(fallback, null);

            try
            {
                string value = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (value.Equals("en", StringComparison.OrdinalIgnoreCase))
                    return new LanguageLoadResult(AppLanguage.English, null);
                if (value.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
                    return new LanguageLoadResult(AppLanguage.SimplifiedChinese, null);
                return new LanguageLoadResult(fallback,
                    new InvalidDataException("The saved language setting is not supported."));
            }
            catch (Exception error)
            {
                return new LanguageLoadResult(fallback, error);
            }
        }

        internal static bool TrySave(string path, AppLanguage language, out Exception error)
        {
            try
            {
                if (String.IsNullOrEmpty(path))
                    throw new ArgumentException("A language settings path is required.", "path");

                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                string value = language == AppLanguage.SimplifiedChinese ? "zh-CN" : "en";
                File.WriteAllText(path, value, new UTF8Encoding(false));
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                return false;
            }
        }
    }

    internal enum TrayStatusFeedback
    {
        None,
        Copied,
        Failed
    }

    internal sealed class TrayStatusModel
    {
        private AppLanguage _language;
        private bool _paused;
        private TrayStatusFeedback _feedback;
        private int _count;

        internal TrayStatusModel(AppLanguage language)
        {
            _language = language;
        }

        internal string Text
        {
            get
            {
                if (_feedback == TrayStatusFeedback.Copied)
                    return FormatCount(AppText.TooltipCopied);
                if (_feedback == TrayStatusFeedback.Failed)
                    return FormatCount(AppText.TooltipFailed);
                return FormatCount(_paused ? AppText.TooltipPaused : AppText.TooltipReady);
            }
        }

        internal void SetCount(int count)
        {
            _count = Math.Max(0, count);
        }

        private string FormatCount(AppText text)
        {
            // NotifyIcon.Text is limited to 63 characters. A compact display
            // keeps the state and count visible even for very large batches.
            string count = _count > 999 ? "999+" : _count.ToString(CultureInfo.InvariantCulture);
            return String.Format(CultureInfo.InvariantCulture,
                AppLocalization.Get(_language, text), count);
        }

        internal void SetLanguage(AppLanguage language)
        {
            _language = language;
        }

        internal void SetPaused(bool paused)
        {
            _paused = paused;
            _feedback = TrayStatusFeedback.None;
        }

        internal void ShowCopied()
        {
            _feedback = TrayStatusFeedback.Copied;
        }

        internal void ShowFailed()
        {
            _feedback = TrayStatusFeedback.Failed;
        }

        internal void ClearFeedback()
        {
            _feedback = TrayStatusFeedback.None;
        }
    }
}
