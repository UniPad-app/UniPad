using System.ComponentModel;
using Avalonia.Media;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextFlowDirection)));

            // Tells the bound captions to re-read themselves. Raised before LanguageChanged so that
            // the view models rebuild their own strings on top of an already-updated interface.
            Text.RaiseAllChanged();

            LanguageChanged?.Invoke();
        }
    }

    /// <summary>True when the active language is written right to left.</summary>
    public bool IsRightToLeft => _language == "fa";

    /// <summary>
    /// Base direction for text runs, bound from XAML on individual text leaves.
    /// <para>
    /// Kept separate from the layout direction on purpose: a panel whose columns must not swap is
    /// pinned to left-to-right, but the captions inside it still have to be laid out with the
    /// language's own base direction. Without that, a left-to-right base direction resolves the
    /// zero-width non-joiner inside Persian words as a neutral boundary, splits the word into
    /// separate runs and orders them the wrong way round.
    /// </para>
    /// </summary>
    public FlowDirection TextFlowDirection =>
        IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>
    /// Wraps a value that is not translated - a number, a unit, a device or profile name, an
    /// exception message - in a first-strong isolate.
    /// <para>
    /// Without it the bidirectional algorithm reorders such a fragment independently of the label
    /// it belongs to, because the surrounding spaces and pipes are neutral: the status bar showed
    /// "Poll: 1000 Hz | cycle 18 us" with the number, the unit and the separator scattered across
    /// the line. An isolate keeps the fragment in one piece and takes its direction from its own
    /// first strong character, so a Latin unit reads left to right and a Persian one right to left
    /// without the caller having to know which.
    /// </para>
    /// </summary>
    public static string Isolate(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $"\u2068{value}\u2069";

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
    public sealed class LocalizedText : INotifyPropertyChanged
    {
        private readonly Strings _owner;

        internal LocalizedText(Strings owner) => _owner = owner;

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Looks up a localised string by key.</summary>
        public string this[string key] => _owner[key];

        /// <summary>
        /// Tells every binding to re-read the indexer.
        /// <para>
        /// The notification has to be raised here rather than on <see cref="Strings"/>: XAML binds
        /// <c>Text[key]</c>, so the indexer belongs to this object, and since this instance is
        /// created once and never replaced, a notification on the owner never reached the captions
        /// that had already been resolved - they kept the previous language until the window was
        /// recreated, which is why switching language appeared to need a restart.
        /// </para>
        /// </summary>
        internal void RaiseAllChanged()
        {
            // "Item[]" is the name an indexer binding listens for; the empty name is the catch-all
            // that means "every property changed", which covers both binding implementations.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
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
        ["player.keyboardMouse"] = "Keyboard & Mouse",

        ["group.leftStick"] = "Left Stick",
        ["group.rightStick"] = "Right Stick",
        ["group.dpad"] = "D-Pad",
        ["group.faceButtons"] = "Face Buttons",
        ["group.shoulders"] = "Shoulders",
        ["group.shouldersL"] = "Shoulder L",
        ["group.shouldersR"] = "Shoulder R",
        ["group.triggers"] = "Triggers",
        ["group.misc"] = "Misc",
        ["group.keyboardMouse"] = "Keyboard & Mouse",

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
        ["bind.back"] = "Back",
        ["bind.guide"] = "Guide",
        ["bind.start"] = "Start",

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
        ["action.updateMappingDb"] = "Update Mapping Database",
        ["action.restartNow"] = "Restart Now",
        ["action.releasePage"] = "Open Release Page",
        ["action.autoDetectAll"] = "Auto-Detect All",

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
        ["about.maintenance"] = "Maintenance",

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
        ["opt.keyboardMouseEnabled"] = "Enable keyboard and mouse input",
        ["opt.mouseSensitivity"] = "Mouse sensitivity",
        ["opt.mouseReturnSpeed"] = "Return to centre",
        ["opt.mouseInvertY"] = "Invert mouse Y",

        ["unit.seconds"] = "s",
        ["unit.hz"] = "Hz",
        ["unit.microseconds"] = "\u00B5s",

        ["status.emulatedDevices"] = "Emulated Devices",
        ["status.other"] = "Other",
        ["status.connected"] = "Connected",
        ["status.controllers"] = "Controllers",
        ["status.xinputSlot"] = "XInput slot",
        ["status.driverMissing"] = "ViGEmBus driver is not installed. Virtual controllers are unavailable.",
        ["status.hidhideMissing"] = "HidHide is not installed. For best results install it to stop double input.",
        ["status.vigemConnected"] = "ViGEmBus: installed and connected",
        ["status.vigemUnreachable"] = "ViGEmBus: installed but unreachable",
        ["status.vigemMissing"] = "ViGEmBus: not installed",
        ["status.vigemUnknown"] = "ViGEmBus: state unknown",
        ["status.hidHideInstalled"] = "HidHide: installed",
        ["status.hidHideMissing"] = "HidHide: not installed (optional but recommended)",
        ["status.hidHideUnknown"] = "HidHide: state unknown",
        ["status.hiddenDevices"] = "hidden",
        ["status.pollRate"] = "Poll",
        ["status.cycle"] = "Cycle",
        ["status.devices"] = "Devices",

        ["msg.autoMapped"] = "Auto-mapping applied. Please review and correct it.",
        ["msg.profileLoaded"] = "Profile loaded:",
        ["msg.inputInitFailed"] = "Input initialisation failed:",
        ["msg.deviceSetupFailed"] = "Device setup failed:",
        ["msg.restartFailed"] = "Could not relaunch UniPad; please start it again manually.",
        ["msg.mappingDbInvalid"] = "Downloaded mapping database looks invalid; keeping the existing one.",
        ["msg.mappingDbUpdated"] = "Mapping database updated. Restart UniPad to apply it.",
        ["msg.mappingDbFailed"] = "Mapping database update failed:",
        ["msg.openPathFailed"] = "Could not open:",
        ["msg.autoMapKeyboard"] = "Applied the default keyboard and mouse layout.",
        ["msg.autoMapSdl"] = "Mapped from the SDL controller database:",
        ["msg.autoMapGuessed"] = "Guessed a mapping. Please review and correct it:",
        ["msg.autoMapFailed"] = "Could not guess a mapping. Please bind manually:",
        ["msg.allCleared"] = "All mappings cleared.",
        ["msg.applying"] = "Applying...",
        ["msg.applyFailed"] = "Apply failed:",
        ["msg.outputEnabled"] = "Output enabled.",
        ["msg.outputDisabled"] = "Output disabled.",
        ["msg.profileCreated"] = "Profile created:",
        ["msg.profileDeleted"] = "Profile deleted:",
        ["msg.profileProtected"] = "The default profile cannot be deleted.",
        ["msg.logLevelRestart"] = "Log level changes take effect after a restart.",
        ["msg.startupEntryFailed"] = "Could not update the Windows startup entry.",
        ["msg.profileSaved"] = "Profile saved.",
        ["msg.noDevice"] = "Select an input device first.",
        ["msg.xinputLimit"] = "Windows provides only 4 XInput slots. Players 5-8 use DualShock 4 output, which XInput-only games will not see.",
        ["msg.antiCheat"] = "Games with kernel-mode anti-cheat may reject virtual controllers. This tool is not designed to bypass any protection.",
        ["msg.keyboardMouseRestart"] = "Restart UniPad for this change to take effect.",
        ["msg.keyboardMouseDoubleInput"] = "Keystrokes still reach the game as well as the virtual pad. Unbind the keyboard controls inside the game if both respond at once.",
        ["msg.keyboardPassthrough"] =
            "Keystrokes still reach the game as well as the virtual pad. Unbind the keyboard controls "
            + "inside the game if both respond at once.",
        ["msg.restartRequired"] = "This change takes effect after a restart.",
        ["msg.updateChecking"] = "Checking GitHub for a newer release...",
        ["msg.upToDate"] = "UniPad is up to date",
        ["msg.updateDownloading"] = "Downloading version",
        ["msg.updateInstalled"] = "Update installed - restart UniPad to use it",
        ["msg.updateFailed"] = "Update failed",
        ["msg.driverDownloading"] = "Downloading driver:",
        ["msg.driverInstalling"] = "Installing driver:",
        ["msg.driverInstallingElevated"] = "Installing driver, approve the Windows prompt:",
        ["msg.driverInstalled"] = "Driver installed:",
        ["msg.driverInstalledRestart"] = "Driver installed, a restart is required:",
        ["msg.driverCancelled"] = "Driver installation was cancelled:",
        ["msg.driverNotSupported"] = "Driver installation is only supported on Windows.",
        ["msg.driverNotBundled"] = "No installer is bundled with this build; please install it manually:",
        ["msg.driverDownloadIncomplete"] = "The downloaded installer looks incomplete; please install it manually:",
        ["msg.driverInstallerMissing"] = "Installer file not found:",
        ["msg.driverTimedOut"] = "The installer did not finish in time:",
        ["msg.driverInstallFailed"] = "Driver installation failed:",

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
        ["player.keyboardMouse"] = "کیبورد و ماوس",

        ["group.leftStick"] = "آنالوگ چپ",
        ["group.rightStick"] = "آنالوگ راست",
        ["group.dpad"] = "دی‌پد",
        ["group.faceButtons"] = "دکمه‌های اصلی",
        ["group.shoulders"] = "دکمه‌های شانه",
        ["group.shouldersL"] = "شانه چپ",
        ["group.shouldersR"] = "شانه راست",
        ["group.triggers"] = "ماشه‌ها",
        ["group.misc"] = "متفرقه",
        ["group.keyboardMouse"] = "کیبورد و ماوس",

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
        ["bind.back"] = "بازگشت",
        ["bind.guide"] = "راهنما",
        ["bind.start"] = "شروع",

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
        ["action.updateMappingDb"] = "به‌روزرسانی پایگاه‌داده نگاشت",
        ["action.restartNow"] = "راه‌اندازی مجدد",
        ["action.releasePage"] = "باز کردن صفحه انتشار",
        ["action.autoDetectAll"] = "شناسایی خودکار همه",

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
        ["about.maintenance"] = "نگهداری",

        ["opt.vibration"] = "لرزش",
        ["opt.emulateStick"] = "شبیه‌سازی آنالوگ چپ با دی‌پد",
        ["opt.outputMode"] = "حالت خروجی",
        ["opt.hidePhysical"] = "مخفی کردن کنترلرهای فیزیکی (HidHide)",
        ["opt.startMinimized"] = "شروع به‌صورت مینیمایز در سینی",
        ["opt.minimizeOnClose"] = "مینیمایز به سینی هنگام بستن",
        ["opt.runAtStartup"] = "اجرا هنگام راه‌اندازی ویندوز",
        ["opt.pollRate"] = "نرخ خوانش",
        ["opt.theme"] = "پوسته",
        ["opt.language"] = "زبان",
        ["opt.verboseLogging"] = "لاگ کامل",
        ["opt.outputEnabled"] = "خروجی فعال",
        ["opt.keyboardMouseEnabled"] = "فعال‌سازی ورودی کیبورد و ماوس",
        ["opt.mouseSensitivity"] = "حساسیت ماوس",
        ["opt.mouseReturnSpeed"] = "زمان بازگشت به مرکز",
        ["opt.mouseInvertY"] = "معکوس کردن محور عمودی ماوس",

        ["unit.seconds"] = "ثانیه",
        ["unit.hz"] = "هرتز",
        ["unit.microseconds"] = "میکروثانیه",

        ["status.emulatedDevices"] = "دستگاه‌های شبیه‌سازی‌شده",
        ["status.other"] = "سایر",
        ["status.connected"] = "متصل",
        ["status.controllers"] = "کنترلرها",
        ["status.xinputSlot"] = "اسلات XInput",
        ["status.driverMissing"] = "درایور ViGEmBus نصب نیست. کنترلر مجازی در دسترس نیست.",
        ["status.hidhideMissing"] = "HidHide نصب نیست. برای بهترین نتیجه آن را نصب کنید تا ورودی دوتایی نشود.",
        ["status.vigemConnected"] = "ViGEmBus: نصب‌شده و متصل",
        ["status.vigemUnreachable"] = "ViGEmBus: نصب‌شده اما در دسترس نیست",
        ["status.vigemMissing"] = "ViGEmBus: نصب نشده",
        ["status.vigemUnknown"] = "ViGEmBus: وضعیت نامشخص",
        ["status.hidHideInstalled"] = "HidHide: نصب‌شده",
        ["status.hidHideMissing"] = "HidHide: نصب نشده (اختیاری اما توصیه می‌شود)",
        ["status.hidHideUnknown"] = "HidHide: وضعیت نامشخص",
        ["status.hiddenDevices"] = "دستگاه پنهان‌شده",
        ["status.pollRate"] = "نرخ خوانش",
        ["status.cycle"] = "چرخه",
        ["status.devices"] = "دستگاه‌ها",

        ["msg.autoMapped"] = "نگاشت خودکار انجام شد — لطفاً بررسی و اصلاح کنید.",
        ["msg.profileLoaded"] = "پروفایل بارگذاری شد:",
        ["msg.inputInitFailed"] = "راه‌اندازی ورودی ناموفق بود:",
        ["msg.deviceSetupFailed"] = "آماده‌سازی دستگاه ناموفق بود:",
        ["msg.restartFailed"] = "UniPad دوباره اجرا نشد؛ لطفاً خودتان آن را اجرا کنید.",
        ["msg.mappingDbInvalid"] = "فایل پایگاه داده دانلودشده معتبر نیست؛ نسخه قبلی حفظ شد.",
        ["msg.mappingDbUpdated"] = "پایگاه داده نگاشت به‌روزرسانی شد. برای اعمال، برنامه را دوباره اجرا کنید.",
        ["msg.mappingDbFailed"] = "به‌روزرسانی پایگاه داده نگاشت ناموفق بود:",
        ["msg.openPathFailed"] = "این مسیر باز نشد:",
        ["msg.autoMapKeyboard"] = "چیدمان پیش‌فرض کیبورد و ماوس اعمال شد.",
        ["msg.autoMapSdl"] = "نگاشت از پایگاه داده کنترلرهای SDL انجام شد:",
        ["msg.autoMapGuessed"] = "نگاشت حدس زده شد؛ لطفاً بررسی و اصلاح کنید:",
        ["msg.autoMapFailed"] = "نگاشت حدس زده نشد؛ لطفاً دستی تعیین کنید:",
        ["msg.allCleared"] = "همه نگاشت‌ها پاک شد.",
        ["msg.applying"] = "در حال اعمال...",
        ["msg.applyFailed"] = "اعمال ناموفق بود:",
        ["msg.outputEnabled"] = "خروجی فعال شد.",
        ["msg.outputDisabled"] = "خروجی غیرفعال شد.",
        ["msg.profileCreated"] = "پروفایل ساخته شد:",
        ["msg.profileDeleted"] = "پروفایل حذف شد:",
        ["msg.profileProtected"] = "پروفایل پیش‌فرض را نمی‌توان حذف کرد.",
        ["msg.logLevelRestart"] = "تغییر سطح لاگ پس از راه‌اندازی مجدد اعمال می‌شود.",
        ["msg.startupEntryFailed"] = "ثبت اجرای خودکار در ویندوز به‌روزرسانی نشد.",
        ["msg.profileSaved"] = "پروفایل ذخیره شد.",
        ["msg.noDevice"] = "ابتدا یک دستگاه ورودی انتخاب کنید.",
        ["msg.xinputLimit"] = "ویندوز فقط ۴ اسلات XInput دارد. بازیکنان ۵ تا ۸ از خروجی DualShock 4 استفاده می‌کنند که بازی‌های صرفاً XInput آن‌ها را نمی‌بینند.",
        ["msg.antiCheat"] = "بازی‌هایی با آنتی‌چیت کرنل‌مود ممکن است کنترلر مجازی را نپذیرند. این ابزار برای دور زدن هیچ محافظتی طراحی نشده است.",
        ["msg.keyboardMouseRestart"] = "برای اعمال این تغییر UniPad را دوباره اجرا کنید.",
        ["msg.keyboardMouseDoubleInput"] = "کلیدهای فشرده‌شده علاوه بر پد مجازی به خود بازی هم می‌رسند. اگر هر دو هم‌زمان واکنش نشان دادند، کنترل‌های کیبورد را داخل بازی بردارید.",
        ["msg.keyboardPassthrough"] =
            "کلیدها هم‌زمان به بازی و به دسته مجازی می‌رسند. اگر هر دو با هم واکنش دادند، کنترل‌های "
            + "کیبورد را داخل خود بازی آزاد کنید.",
        ["msg.restartRequired"] = "این تغییر پس از راه‌اندازی مجدد اعمال می‌شود.",
        ["msg.updateChecking"] = "در حال بررسی نسخه جدید در گیت‌هاب...",
        ["msg.upToDate"] = "UniPad به‌روز است",
        ["msg.updateDownloading"] = "دانلود نسخه",
        ["msg.updateInstalled"] = "به‌روزرسانی نصب شد — برنامه را دوباره اجرا کنید",
        ["msg.updateFailed"] = "به‌روزرسانی ناموفق بود",
        ["msg.driverDownloading"] = "در حال دانلود درایور:",
        ["msg.driverInstalling"] = "در حال نصب درایور:",
        ["msg.driverInstallingElevated"] = "در حال نصب درایور؛ پیام ویندوز را تأیید کنید:",
        ["msg.driverInstalled"] = "درایور نصب شد:",
        ["msg.driverInstalledRestart"] = "درایور نصب شد؛ راه‌اندازی مجدد لازم است:",
        ["msg.driverCancelled"] = "نصب درایور لغو شد:",
        ["msg.driverNotSupported"] = "نصب درایور فقط در ویندوز پشتیبانی می‌شود.",
        ["msg.driverNotBundled"] = "نصب‌کننده همراه این نسخه نیست؛ لطفاً دستی نصب کنید:",
        ["msg.driverDownloadIncomplete"] = "فایل دانلودشده ناقص به نظر می‌رسد؛ لطفاً دستی نصب کنید:",
        ["msg.driverInstallerMissing"] = "فایل نصب‌کننده پیدا نشد:",
        ["msg.driverTimedOut"] = "نصب‌کننده در زمان مقرر تمام نشد:",
        ["msg.driverInstallFailed"] = "نصب درایور ناموفق بود:",

    };
}
