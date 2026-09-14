using System.ComponentModel;

namespace UniPad.App.Localization;

/// <summary>
/// Minimal in-code localisation table.
/// <para>
/// Satellite resource assemblies are avoided on purpose: they complicate single-file publishing.
/// Two languages in a dictionary is entirely adequate here, and switching language raises
/// <see cref="INotifyPropertyChanged"/> for the indexer so bound text updates live.
/// </para>
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    /// <summary>Shared instance bound from XAML.</summary>
    public static Strings Instance { get; } = new();

    private string _language = "en";

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the active language changes.</summary>
    public event Action? LanguageChanged;

    /// <summary>Active language code, <c>en</c> or <c>fa</c>.</summary>
    public string Language
    {
        get => _language;
        set
        {
            var normalised = value is "fa" ? "fa" : "en";
            if (_language == normalised)
            {
                return;
            }

            _language = normalised;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>True when the active language is written right to left.</summary>
    public bool IsRightToLeft => _language == "fa";

    /// <summary>Looks up a string by key, falling back to English and then to the key itself.</summary>
    public string this[string key]
    {
        get
        {
            if (_language == "fa" && Persian.TryGetValue(key, out var fa))
            {
                return fa;
            }

            return English.GetValueOrDefault(key, key);
        }
    }

    /// <summary>
    /// Indexable façade used from XAML as <c>Text[key]</c>. Avalonia's compiled bindings cannot
    /// bind to the default indexer of a source object directly, so the lookup is exposed through
    /// a named property whose value is itself indexable.
    /// </summary>
    public LocalizedText Text { get; }

    /// <summary>Static lookup helper for code-behind use.</summary>
    public static string Get(string key) => Instance[key];

    private Strings() => Text = new LocalizedText(this);

    /// <summary>Read-only indexable view over the active language table.</summary>
    public sealed class LocalizedText
    {
        private readonly Strings _owner;

        internal LocalizedText(Strings owner) => _owner = owner;

        /// <summary>Looks up a localised string by key.</summary>
        public string this[string key] => _owner[key];
    }

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["app.title"] = "UniPad Configuration",
        ["app.name"] = "UniPad",

        ["nav.general"] = "General",
        ["nav.controls"] = "Controls",
        ["nav.advanced"] = "Advanced",
        ["nav.about"] = "About",

        ["tab.player"] = "Player",
        ["tab.advanced"] = "Advanced",

        ["player.connect"] = "Connect Controller",
        ["player.inputDevice"] = "Input Device",
        ["player.profile"] = "Profile",
        ["player.controllerType"] = "Controller Type",
        ["player.any"] = "Any",
        ["player.notConnected"] = "Not connected",

        ["group.leftStick"] = "Left Stick",
        ["group.rightStick"] = "Right Stick",
        ["group.dpad"] = "D-Pad",
        ["group.faceButtons"] = "Face Buttons",
        ["group.shoulders"] = "Shoulders",
        ["group.shouldersL"] = "Shoulder L",
        ["group.shouldersR"] = "Shoulder R",
        ["group.triggers"] = "Triggers",
        ["group.misc"] = "Misc",

        ["bind.up"] = "Up",
        ["bind.down"] = "Down",
        ["bind.left"] = "Left",
        ["bind.right"] = "Right",
        ["bind.pressed"] = "Pressed",
        ["bind.modifier"] = "Modifier",
        ["bind.range"] = "Range",
        ["bind.deadzone"] = "Deadzone",
        ["bind.notSet"] = "[not set]",
        ["bind.pressKey"] = "[press key]",

        ["menu.clear"] = "Clear",
        ["menu.invertAxis"] = "Invert axis",
        ["menu.toggle"] = "Toggle",
        ["menu.setThreshold"] = "Set threshold...",
        ["menu.swapWith"] = "Swap with...",

        ["action.save"] = "Save",
        ["action.new"] = "New",
        ["action.delete"] = "Delete",
        ["action.rename"] = "Rename",
        ["action.refresh"] = "Refresh",
        ["action.autoMap"] = "Auto Map",
        ["action.clearAll"] = "Clear All",
        ["action.defaults"] = "Defaults",
        ["action.ok"] = "OK",
        ["action.cancel"] = "Cancel",
        ["action.apply"] = "Apply",
        ["action.close"] = "Close",
        ["action.configure"] = "Configure",
        ["action.identify"] = "Identify",
        ["action.install"] = "Install",
        ["action.dismiss"] = "Dismiss",
        ["action.update"] = "Check for Updates",
        ["action.openLogs"] = "Open Logs Folder",
        ["action.openDataFolder"] = "Open Data Folder",

        ["tray.open"] = "Open UniPad",
        ["tray.toggle"] = "Enable / Disable Output",
        ["tray.exit"] = "Exit",
        ["tray.tooltip"] = "UniPad - Universal Controller Mapper",

        ["about.heading"] = "UniPad",
        ["about.tagline"] = "Universal controller mapper for Windows.",
        ["about.version"] = "Version",
        ["about.description"] =
            "UniPad presents any input device - modern gamepads, analogue-less retro pads, arcade sticks, "
            + "wheels and no-name USB adapters - to Windows as a standard Xbox 360 or DualShock 4 controller, "
            + "so games that only understand XInput work with the hardware you already own.",
        ["about.credits"] = "Third-party components",
        ["about.creditSdl"] = "SDL3 - device enumeration and raw input (zlib licence)",
        ["about.creditViGEm"] = "ViGEmBus / ViGEm.NET by Nefarius Software Solutions (MIT / BSD-3)",
        ["about.creditHidHide"] = "HidHide by Nefarius Software Solutions (MIT)",
        ["about.creditAvalonia"] = "Avalonia UI (MIT)",
        ["about.creditSerilog"] = "Serilog (Apache-2.0)",
        ["about.creditToolkit"] = "CommunityToolkit.Mvvm (MIT)",
        ["about.creditDb"] = "SDL_GameControllerDB community mapping database (Zlib)",
        ["about.disclaimer"] =
            "UniPad is not an emulator and contains no game code. The interface layout takes visual "
            + "inspiration from familiar controller configuration dialogs, but every asset, style and line "
            + "of code here is original work.",
        ["about.dataFolder"] = "Data folder",

        ["opt.vibration"] = "Vibration",
        ["opt.emulateStick"] = "Emulate Left Stick with D-Pad",
        ["opt.outputMode"] = "Output Mode",
        ["opt.hidePhysical"] = "Hide physical controllers (HidHide)",
        ["opt.startMinimized"] = "Start minimized to tray",
        ["opt.minimizeOnClose"] = "Minimize to tray on close",
        ["opt.runAtStartup"] = "Run at Windows startup",
        ["opt.pollRate"] = "Polling rate",
        ["opt.theme"] = "Theme",
        ["opt.language"] = "Language",
        ["opt.verboseLogging"] = "Verbose logging",
        ["opt.outputEnabled"] = "Output enabled",

        ["status.emulatedDevices"] = "Emulated Devices",
        ["status.other"] = "Other",
        ["status.connected"] = "Connected",
        ["status.controllers"] = "Controllers",
        ["status.xinputSlot"] = "XInput slot",
        ["status.driverMissing"] = "ViGEmBus driver is not installed. Virtual controllers are unavailable.",
        ["status.hidhideMissing"] = "HidHide is not installed. For best results install it to stop double input.",
        ["status.pollRate"] = "Poll",
        ["status.devices"] = "Devices",

        ["msg.autoMapped"] = "Auto-mapping applied. Please review and correct it.",
        ["msg.profileSaved"] = "Profile saved.",
        ["msg.noDevice"] = "Select an input device first.",
        ["msg.xinputLimit"] = "Windows provides only 4 XInput slots. Players 5-8 use DualShock 4 output, which XInput-only games will not see.",
        ["msg.antiCheat"] = "Games with kernel-mode anti-cheat may reject virtual controllers. This tool is not designed to bypass any protection.",

    };

    private static readonly Dictionary<string, string> Persian = new(StringComparer.Ordinal)
    {
        ["app.title"] = "تنظیمات UniPad",
        ["app.name"] = "UniPad",

        ["nav.general"] = "عمومی",
        ["nav.controls"] = "کنترل‌ها",
        ["nav.advanced"] = "پیشرفته",
        ["nav.about"] = "درباره",

        ["tab.player"] = "بازیکن",
        ["tab.advanced"] = "پیشرفته",

        ["player.connect"] = "اتصال کنترلر",
        ["player.inputDevice"] = "دستگاه ورودی",
        ["player.profile"] = "پروفایل",
        ["player.controllerType"] = "نوع کنترلر",
        ["player.any"] = "هر کدام",
        ["player.notConnected"] = "متصل نیست",

        ["group.leftStick"] = "آنالوگ چپ",
        ["group.rightStick"] = "آنالوگ راست",
        ["group.dpad"] = "دی‌پد",
        ["group.faceButtons"] = "دکمه‌های اصلی",
        ["group.shoulders"] = "دکمه‌های شانه",
        ["group.shouldersL"] = "شانه چپ",
        ["group.shouldersR"] = "شانه راست",
        ["group.triggers"] = "ماشه‌ها",
        ["group.misc"] = "متفرقه",

        ["bind.up"] = "بالا",
        ["bind.down"] = "پایین",
        ["bind.left"] = "چپ",
        ["bind.right"] = "راست",
        ["bind.pressed"] = "فشرده",
        ["bind.modifier"] = "تعدیل‌گر",
        ["bind.range"] = "بازه",
        ["bind.deadzone"] = "ناحیه مرده",
        ["bind.notSet"] = "[تنظیم نشده]",
        ["bind.pressKey"] = "[کلید را بزنید]",

        ["menu.clear"] = "پاک کردن",
        ["menu.invertAxis"] = "معکوس کردن محور",
        ["menu.toggle"] = "حالت ضامن‌دار",
        ["menu.setThreshold"] = "تنظیم آستانه...",
        ["menu.swapWith"] = "جابه‌جایی با...",

        ["action.save"] = "ذخیره",
        ["action.new"] = "جدید",
        ["action.delete"] = "حذف",
        ["action.rename"] = "تغییر نام",
        ["action.refresh"] = "بازخوانی",
        ["action.autoMap"] = "نگاشت خودکار",
        ["action.clearAll"] = "پاک کردن همه",
        ["action.defaults"] = "پیش‌فرض‌ها",
        ["action.ok"] = "تأیید",
        ["action.cancel"] = "انصراف",
        ["action.apply"] = "اعمال",
        ["action.close"] = "بستن",
        ["action.configure"] = "تنظیم",
        ["action.identify"] = "شناسایی",
        ["action.install"] = "نصب",
        ["action.dismiss"] = "بستن",
        ["action.update"] = "بررسی به‌روزرسانی",
        ["action.openLogs"] = "باز کردن پوشه لاگ",
        ["action.openDataFolder"] = "باز کردن پوشه داده",

        ["tray.open"] = "باز کردن UniPad",
        ["tray.toggle"] = "فعال / غیرفعال کردن خروجی",
        ["tray.exit"] = "خروج",
        ["tray.tooltip"] = "UniPad - نگاشت‌گر همه‌کاره کنترلر",

        ["about.heading"] = "UniPad",
        ["about.tagline"] = "نگاشت‌گر همه‌کاره کنترلر برای ویندوز.",
        ["about.version"] = "نسخه",
        ["about.description"] =
            "UniPad هر دستگاه ورودی — از گیم‌پدهای مدرن و دسته‌های قدیمی بدون آنالوگ تا استیک‌های آرکید، "
            + "فرمان‌ها و آداپتورهای USB بی‌نام — را به شکل یک کنترلر استاندارد Xbox 360 یا DualShock 4 به ویندوز "
            + "معرفی می‌کند، تا بازی‌هایی که فقط XInput را می‌شناسند با همان سخت‌افزاری که دارید کار کنند.",
        ["about.credits"] = "اجزای شخص ثالث",
        ["about.creditSdl"] = "SDL3 — شناسایی دستگاه و ورودی خام (مجوز zlib)",
        ["about.creditViGEm"] = "ViGEmBus / ViGEm.NET از Nefarius Software Solutions (MIT / BSD-3)",
        ["about.creditHidHide"] = "HidHide از Nefarius Software Solutions (MIT)",
        ["about.creditAvalonia"] = "Avalonia UI (MIT)",
        ["about.creditSerilog"] = "Serilog (Apache-2.0)",
        ["about.creditToolkit"] = "CommunityToolkit.Mvvm (MIT)",
        ["about.creditDb"] = "پایگاه‌داده نگاشت انجمنی SDL_GameControllerDB (Zlib)",
        ["about.disclaimer"] =
            "UniPad یک شبیه‌ساز نیست و هیچ کد بازی در آن وجود ندارد. چیدمان رابط کاربری از پنجره‌های آشنای "
            + "تنظیم کنترلر الهام بصری گرفته است، اما تمام دارایی‌ها، استایل‌ها و خطوط کد این برنامه اثر اصیل خودش است.",
        ["about.dataFolder"] = "پوشه داده",

        ["opt.vibration"] = "لرزش",
        ["opt.emulateStick"] = "شبیه‌سازی آنالوگ چپ با دی‌پد",
        ["opt.outputMode"] = "حالت خروجی",
        ["opt.hidePhysical"] = "مخفی کردن کنترلرهای فیزیکی (HidHide)",
        ["opt.startMinimized"] = "شروع به‌صورت مینیمایز در سینی",
        ["opt.minimizeOnClose"] = "مینیمایز به سینی هنگام بستن",
        ["opt.runAtStartup"] = "اجرا هنگام راه‌اندازی ویندوز",
        ["opt.pollRate"] = "نرخ نظرسنجی",
        ["opt.theme"] = "پوسته",
        ["opt.language"] = "زبان",
        ["opt.verboseLogging"] = "لاگ کامل",
        ["opt.outputEnabled"] = "خروجی فعال",

        ["status.emulatedDevices"] = "دستگاه‌های شبیه‌سازی‌شده",
        ["status.other"] = "سایر",
        ["status.connected"] = "متصل",
        ["status.controllers"] = "کنترلرها",
        ["status.xinputSlot"] = "اسلات XInput",
        ["status.driverMissing"] = "درایور ViGEmBus نصب نیست. کنترلر مجازی در دسترس نیست.",
        ["status.hidhideMissing"] = "HidHide نصب نیست. برای بهترین نتیجه آن را نصب کنید تا ورودی دوتایی نشود.",
        ["status.pollRate"] = "نظرسنجی",
        ["status.devices"] = "دستگاه‌ها",

        ["msg.autoMapped"] = "نگاشت خودکار انجام شد — لطفاً بررسی و اصلاح کنید.",
        ["msg.profileSaved"] = "پروفایل ذخیره شد.",
        ["msg.noDevice"] = "ابتدا یک دستگاه ورودی انتخاب کنید.",
        ["msg.xinputLimit"] = "ویندوز فقط ۴ اسلات XInput دارد. بازیکنان ۵ تا ۸ از خروجی DualShock 4 استفاده می‌کنند که بازی‌های صرفاً XInput آن‌ها را نمی‌بینند.",
        ["msg.antiCheat"] = "بازی‌هایی با آنتی‌چیت کرنل‌مود ممکن است کنترلر مجازی را نپذیرند. این ابزار برای دور زدن هیچ محافظتی طراحی نشده است.",

    };
}
