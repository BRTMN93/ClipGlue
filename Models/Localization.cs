namespace ClipGlue.Models;

/// <summary>
/// The 13 languages offered by the language switcher: the 10 most widely
/// spoken languages in the world (by total speaker count - Mandarin,
/// English, Hindi, Spanish, French, Arabic, Bengali, Portuguese, Russian,
/// Urdu), plus Polish, Vietnamese and Thai added on top since none of the
/// three are in that top 10.
/// </summary>
public enum AppLanguage
{
    English, Chinese, Hindi, Spanish, French, Arabic, Bengali, Portuguese, Russian, Urdu,
    Polish, Vietnamese, Thai,
}

/// <summary>
/// <paramref name="Badge"/> is a short ISO 639-1-ish code (EN, ZH, HI, ...)
/// shown next to the native name in the picker - NOT a flag emoji. WPF's
/// text layout engine does not apply the font shaping (GSUB ligature
/// substitution) that combines a pair of regional-indicator codepoints into
/// a single flag glyph, so a flag emoji string renders as its two bare
/// letters anyway (confirmed by screenshot: "🇬🇧" showed up as plain "GB").
/// Using an explicit two-letter code and styling it as a small badge makes
/// that same rendering look deliberate instead of like a broken emoji.
/// </summary>
public sealed record LanguageInfo(AppLanguage Code, string NativeName, string Badge);

/// <summary>
/// UI text lookup + the currently selected language. Persisted to the same
/// shared <c>%APPDATA%\ClipGlue\config.json</c> that <see cref="ConfigStore"/>
/// already uses for last-used folders, under the "language" key.
///
/// Only the main window's static chrome (title, header, toolbar/action
/// buttons, empty-state label, dialogs raised directly from MainWindow) is
/// wired through <see cref="T"/> - the Trim range / Project preview windows
/// and the ffmpeg progress log stay English-only for now. See
/// PROJECT_STATE.md for the exact list of what is and isn't covered.
/// </summary>
public static class Loc
{
    /// <summary>Display order for the language picker: the 10 most-spoken
    /// languages first, then the three added on top.</summary>
    public static readonly IReadOnlyList<LanguageInfo> Languages = new List<LanguageInfo>
    {
        new(AppLanguage.English, "English", "EN"),
        new(AppLanguage.Chinese, "中文", "ZH"),
        new(AppLanguage.Hindi, "हिन्दी", "HI"),
        new(AppLanguage.Spanish, "Español", "ES"),
        new(AppLanguage.French, "Français", "FR"),
        new(AppLanguage.Arabic, "العربية", "AR"),
        new(AppLanguage.Bengali, "বাংলা", "BN"),
        new(AppLanguage.Portuguese, "Português", "PT"),
        new(AppLanguage.Russian, "Русский", "RU"),
        new(AppLanguage.Urdu, "اردو", "UR"),
        new(AppLanguage.Polish, "Polski", "PL"),
        new(AppLanguage.Vietnamese, "Tiếng Việt", "VI"),
        new(AppLanguage.Thai, "ไทย", "TH"),
    };

    public static AppLanguage Current { get; private set; } = AppLanguage.English;

    /// <summary>Arabic and Urdu are the only right-to-left languages in the
    /// picker. Consumers use this to mirror the header layout (description
    /// text moves to the right, the language button to the left) - see
    /// MainWindow.ApplyLanguage. Deliberately scoped to the header only:
    /// mirroring the rest of the window (file list drag handles, custom
    /// OnRender timeline/progress controls) risks visual corruption in
    /// controls that draw themselves with hardcoded LTR coordinates, and
    /// was not asked for.</summary>
    public static bool IsRtl => Current is AppLanguage.Arabic or AppLanguage.Urdu;

    /// <summary>Fired after <see cref="Current"/> changes and the new choice
    /// has been persisted - subscribers re-pull every string they show via
    /// <see cref="T"/>.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Loads the persisted language choice, if any. Call once at
    /// startup before the first window is built.</summary>
    public static void Init()
    {
        var config = ConfigStore.Load();
        if (config.TryGetValue("language", out var code) && Enum.TryParse<AppLanguage>(code, out var lang))
            Current = lang;
    }

    public static void SetLanguage(AppLanguage lang)
    {
        if (Current == lang) return;
        Current = lang;
        ConfigStore.Save(("language", lang.ToString()));
        LanguageChanged?.Invoke();
    }

    /// <summary>Looks up <paramref name="key"/> in the current language,
    /// falling back to English and then to the key itself so a missing
    /// translation never shows up as a blank label.</summary>
    public static string T(string key)
    {
        if (Strings.TryGetValue(key, out var byLang))
        {
            if (byLang.TryGetValue(Current, out var s)) return s;
            if (byLang.TryGetValue(AppLanguage.English, out var en)) return en;
        }
        return key;
    }

    // Column order every L(...) call below is written in - matches
    // Languages above so each translation lines up with its inline comment.
    private static readonly AppLanguage[] Order =
    {
        AppLanguage.English, AppLanguage.Chinese, AppLanguage.Hindi, AppLanguage.Spanish, AppLanguage.French,
        AppLanguage.Arabic, AppLanguage.Bengali, AppLanguage.Portuguese, AppLanguage.Russian, AppLanguage.Urdu,
        AppLanguage.Polish, AppLanguage.Vietnamese, AppLanguage.Thai,
    };

    private static Dictionary<AppLanguage, string> L(
        string en, string zh, string hi, string es, string fr, string ar, string bn, string pt, string ru,
        string ur, string pl, string vi, string th)
    {
        var values = new[] { en, zh, hi, es, fr, ar, bn, pt, ru, ur, pl, vi, th };
        var d = new Dictionary<AppLanguage, string>(Order.Length);
        for (int i = 0; i < Order.Length; i++) d[Order[i]] = values[i];
        return d;
    }

    private static readonly Dictionary<string, Dictionary<AppLanguage, string>> Strings = new()
    {
        ["WindowTitle"] = L(
            "ClipGlue - trim and join video clips",
            "ClipGlue - 剪辑与合并视频",
            "ClipGlue - वीडियो ट्रिम और जोड़ें",
            "ClipGlue - recorta y une videos",
            "ClipGlue - découper et assembler des vidéos",
            "ClipGlue - قص ودمج مقاطع الفيديو",
            "ClipGlue - ভিডিও ছাঁটাই ও যুক্ত করুন",
            "ClipGlue - cortar e unir vídeos",
            "ClipGlue - обрезка и склейка видео",
            "ClipGlue - ویڈیو تراشیں اور جوڑیں",
            "ClipGlue - przycinanie i łączenie klipów wideo",
            "ClipGlue - cắt và ghép video",
            "ClipGlue - ตัดต่อและรวมวิดีโอ"),

        ["HeaderTitle"] = L(
            "How it works", "使用说明", "यह कैसे काम करता है", "Cómo funciona", "Comment ça marche",
            "كيف يعمل", "এটি কীভাবে কাজ করে", "Como funciona", "Как это работает", "یہ کیسے کام کرتا ہے",
            "Jak to działa", "Cách hoạt động", "วิธีการทำงาน"),

        ["HeaderLine1"] = L(
            "For each file, enter the time ranges to KEEP, separated by commas.",
            "对每个文件，输入要保留的时间段，用逗号分隔。",
            "प्रत्येक फ़ाइल के लिए, रखे जाने वाले समय अंतराल कॉमा से अलग करके दर्ज करें।",
            "Para cada archivo, introduce los rangos de tiempo a CONSERVAR, separados por comas.",
            "Pour chaque fichier, saisissez les plages horaires à CONSERVER, séparées par des virgules.",
            "لكل ملف، أدخل النطاقات الزمنية المراد الاحتفاظ بها، مفصولة بفواصل.",
            "প্রতিটি ফাইলের জন্য, রাখতে চাওয়া সময়সীমা কমা দিয়ে আলাদা করে লিখুন।",
            "Para cada ficheiro, indique os intervalos de tempo a MANTER, separados por vírgulas.",
            "Для каждого файла укажите диапазоны времени, которые нужно СОХРАНИТЬ, через запятую.",
            "ہر فائل کے لیے، رکھے جانے والے وقت کے حصے کوما سے الگ کر کے درج کریں۔",
            "Dla każdego pliku podaj zakresy czasu do ZACHOWANIA, oddzielone przecinkami.",
            "Với mỗi tệp, nhập các khoảng thời gian cần GIỮ LẠI, cách nhau bằng dấu phẩy.",
            "สำหรับแต่ละไฟล์ ให้ป้อนช่วงเวลาที่ต้องการเก็บไว้ คั่นด้วยเครื่องหมายจุลภาค"),

        // The dash used to be auto-inserted after twelve typed digits, and this
        // string used to promise that. It cannot be: a file longer than 99:59
        // has three-digit minutes, so "where does the start end" is only
        // knowable from a dash the user typed - see TimeUtils.FormatRangeSegment.
        ["HeaderLine2"] = L(
            "Just type digits - the colons are added for you automatically; type the dash between start and end, and a comma to start a new range.",
            "只需输入数字——冒号会自动为您添加；开始与结束之间的短横线、以及开始新范围的逗号请自己输入。",
            "बस अंक टाइप करें - कोलन अपने आप जुड़ जाते हैं; शुरुआत और अंत के बीच डैश तथा नई रेंज के लिए कॉमा खुद टाइप करें।",
            "Solo escribe los dígitos: los dos puntos se añaden automáticamente; escribe tú mismo el guion entre el inicio y el fin, y una coma para empezar un nuevo rango.",
            "Il suffit de taper les chiffres - les deux-points sont ajoutés automatiquement ; tapez vous-même le tiret entre le début et la fin, et une virgule pour commencer une nouvelle plage.",
            "ما عليك سوى كتابة الأرقام - تُضاف النقطتان تلقائياً؛ اكتب بنفسك الشرطة بين البداية والنهاية، وفاصلة لبدء نطاق جديد.",
            "শুধু সংখ্যা টাইপ করুন - কোলন নিজে থেকেই যোগ হয়; শুরু ও শেষের মাঝে ড্যাশ এবং নতুন রেঞ্জ শুরু করতে কমা নিজে টাইপ করুন।",
            "Basta digitar os números - os dois pontos são adicionados automaticamente; escreva o traço entre o início e o fim, e uma vírgula para iniciar um novo intervalo.",
            "Просто вводите цифры - двоеточия добавляются автоматически; тире между началом и концом, а также запятую для нового диапазона введите сами.",
            "بس ہندسے ٹائپ کریں - کولن خودبخود شامل ہو جاتے ہیں؛ آغاز اور اختتام کے درمیان ڈیش اور نئی رینج کے لیے کوما خود ٹائپ کریں۔",
            "Wystarczy wpisywać cyfry - dwukropki dodają się same; myślnik między początkiem a końcem oraz przecinek przed nowym zakresem wpisz samodzielnie.",
            "Chỉ cần gõ các chữ số - dấu hai chấm được tự động thêm vào; hãy tự gõ dấu gạch ngang giữa điểm đầu và điểm cuối, và dấu phẩy để bắt đầu một khoảng mới.",
            "แค่พิมพ์ตัวเลข - เครื่องหมายโคลอนจะถูกเพิ่มให้อัตโนมัติ ส่วนขีดกลางระหว่างจุดเริ่มกับจุดสิ้นสุดและจุลภาคสำหรับช่วงใหม่ต้องพิมพ์เอง"),

        ["HeaderExamplePrefix"] = L(
            "Example:", "示例：", "उदाहरण:", "Ejemplo:", "Exemple :", "مثال:", "উদাহরণ:", "Exemplo:",
            "Пример:", "مثال:", "Przykład:", "Ví dụ:", "ตัวอย่าง:"),

        ["HeaderLine4"] = L(
            "Everything outside the given ranges is removed.",
            "所给范围之外的所有内容都会被删除。",
            "दिए गए अंतराल के बाहर सब कुछ हटा दिया जाता है।",
            "Todo lo que quede fuera de los rangos indicados se elimina.",
            "Tout ce qui se trouve en dehors des plages indiquées est supprimé.",
            "يتم حذف كل ما هو خارج النطاقات المحددة.",
            "নির্দিষ্ট সীমার বাইরের সবকিছু মুছে ফেলা হয়।",
            "Tudo o que estiver fora dos intervalos indicados é removido.",
            "Всё, что находится вне указанных диапазонов, удаляется.",
            "دی گئی حدود سے باہر ہر چیز حذف کر دی جاتی ہے۔",
            "Wszystko poza podanymi zakresami zostaje usunięte.",
            "Mọi thứ nằm ngoài các khoảng đã cho sẽ bị xóa.",
            "ทุกอย่างที่อยู่นอกช่วงที่กำหนดจะถูกลบออก"),

        ["HeaderLine5"] = L(
            "Or click ▷ next to a file to pick ranges visually on a timeline.",
            "或点击文件旁的 ▷ 在时间轴上直观选择范围。",
            "या रेंज को टाइमलाइन पर देखकर चुनने के लिए फ़ाइल के बगल में ▷ पर क्लिक करें।",
            "O haz clic en ▷ junto a un archivo para elegir los rangos visualmente en una línea de tiempo.",
            "Ou cliquez sur ▷ à côté d'un fichier pour choisir les plages visuellement sur une chronologie.",
            "أو انقر على ▷ بجانب الملف لاختيار النطاقات بصرياً على الخط الزمني.",
            "অথবা টাইমলাইনে দৃশ্যত রেঞ্জ বাছাই করতে ফাইলের পাশের ▷ চাপুন।",
            "Ou clique em ▷ junto a um ficheiro para escolher os intervalos visualmente numa linha do tempo.",
            "Или нажмите ▷ рядом с файлом, чтобы выбрать диапазоны наглядно на шкале времени.",
            "یا فائل کے پاس ▷ پر کلک کر کے ٹائم لائن پر بصری طور پر رینج منتخب کریں۔",
            "Albo kliknij ▷ obok pliku, żeby wybrać zakresy wizualnie na osi czasu.",
            "Hoặc nhấp vào ▷ bên cạnh tệp để chọn khoảng thời gian trực quan trên dòng thời gian.",
            "หรือคลิก ▷ ข้างไฟล์เพื่อเลือกช่วงเวลาแบบเห็นภาพบนไทม์ไลน์"),

        ["BtnAddFiles"] = L(
            "Add video files", "添加视频文件", "वीडियो फ़ाइलें जोड़ें", "Añadir archivos de vídeo",
            "Ajouter des fichiers vidéo", "إضافة ملفات فيديو", "ভিডিও ফাইল যোগ করুন",
            "Adicionar ficheiros de vídeo", "Добавить видеофайлы", "ویڈیو فائلیں شامل کریں",
            "Dodaj pliki wideo", "Thêm tệp video", "เพิ่มไฟล์วิดีโอ"),

        ["BtnSaveProject"] = L(
            "Save project", "保存项目", "प्रोजेक्ट सहेजें", "Guardar proyecto", "Enregistrer le projet",
            "حفظ المشروع", "প্রকল্প সংরক্ষণ করুন", "Guardar projeto", "Сохранить проект",
            "پراجیکٹ محفوظ کریں", "Zapisz projekt", "Lưu dự án", "บันทึกโปรเจกต์"),

        ["BtnLoadProject"] = L(
            "Load project", "加载项目", "प्रोजेक्ट लोड करें", "Cargar proyecto", "Charger le projet",
            "تحميل المشروع", "প্রকল্প লোড করুন", "Carregar projeto", "Загрузить проект",
            "پراجیکٹ لوڈ کریں", "Wczytaj projekt", "Tải dự án", "โหลดโปรเจกต์"),

        ["BtnPreviewProject"] = L(
            "Preview project", "预览项目", "प्रोजेक्ट पूर्वावलोकन", "Previsualizar proyecto",
            "Aperçu du projet", "معاينة المشروع", "প্রকল্প পূর্বরূপ", "Pré-visualizar projeto",
            "Предпросмотр проекта", "پراجیکٹ کا پیش منظر", "Podgląd projektu", "Xem trước dự án",
            "ดูตัวอย่างโปรเจกต์"),

        ["BtnHidePreview"] = L(
            "Hide preview", "隐藏预览", "पूर्वावलोकन छिपाएं", "Ocultar vista previa", "Masquer l'aperçu",
            "إخفاء المعاينة", "পূর্বরূপ লুকান", "Ocultar pré-visualização", "Скрыть предпросмотр",
            "پیش منظر چھپائیں", "Ukryj podgląd", "Ẩn xem trước", "ซ่อนตัวอย่าง"),

        ["PreviewProjectTooltip"] = L(
            "Play every range of every file back to back, as the output will be",
            "按顺序连续播放每个文件的每个范围，与最终输出效果一致",
            "आउटपुट जैसा ही, हर फ़ाइल की हर रेंज को क्रम से चलाएं",
            "Reproduce todos los rangos de todos los archivos seguidos, tal como será el resultado",
            "Lit toutes les plages de tous les fichiers à la suite, comme le sera le résultat",
            "تشغيل كل نطاق من كل ملف تباعاً، تماماً كما سيكون الناتج",
            "প্রতিটি ফাইলের সব রেঞ্জ পরপর চালানো হবে, ঠিক যেমন আউটপুট হবে",
            "Reproduz todos os intervalos de todos os ficheiros seguidos, tal como será o resultado",
            "Проигрывает все диапазоны всех файлов подряд, так же, как будет выглядеть результат",
            "ہر فائل کی ہر رینج کو ترتیب سے چلائیں، جیسا آؤٹ پٹ ہوگا",
            "Odtwarza kolejno wszystkie zakresy wszystkich plików, dokładnie tak jak będzie wyglądał wynik",
            "Phát lần lượt mọi khoảng của mọi tệp, giống như kết quả đầu ra sẽ có",
            "เล่นทุกช่วงของทุกไฟล์ต่อเนื่องกัน เหมือนกับผลลัพธ์ที่จะได้"),

        ["EmptyListLabel"] = L(
            "No files yet - click \u201c{0}\u201d to start.",
            "还没有文件 —— 点击“{0}”开始。",
            "अभी कोई फ़ाइल नहीं - शुरू करने के लिए “{0}” पर क्लिक करें।",
            "Aún no hay archivos: haz clic en «{0}» para empezar.",
            "Aucun fichier pour l'instant - cliquez sur « {0} » pour commencer.",
            "لا توجد ملفات بعد - انقر على «{0}» للبدء.",
            "এখনও কোনো ফাইল নেই - শুরু করতে “{0}” চাপুন।",
            "Ainda não há ficheiros - clique em “{0}” para começar.",
            "Пока нет файлов - нажмите «{0}», чтобы начать.",
            "ابھی کوئی فائل نہیں - شروع کرنے کے لیے “{0}” پر کلک کریں۔",
            "Brak plików - kliknij „{0}”, aby zacząć.",
            "Chưa có tệp nào - nhấp vào “{0}” để bắt đầu.",
            "ยังไม่มีไฟล์ - คลิก “{0}” เพื่อเริ่มต้น"),

        ["ConsoleHeader"] = L(
            "Console", "控制台", "कंसोल", "Consola", "Console", "وحدة التحكم", "কনসোল", "Consola",
            "Консоль", "کنسول", "Konsola", "Bảng điều khiển", "คอนโซล"),

        ["BtnStart"] = L(
            "START", "开始", "शुरू करें", "INICIAR", "DÉMARRER", "بدء", "শুরু", "INICIAR", "СТАРТ",
            "شروع کریں", "START", "BẮT ĐẦU", "เริ่ม"),

        ["BtnStop"] = L(
            "STOP", "停止", "रोकें", "DETENER", "ARRÊTER", "إيقاف", "থামান", "PARAR", "СТОП",
            "روکیں", "STOP", "DỪNG", "หยุด"),

        ["BtnStopping"] = L(
            "Stopping…", "正在停止…", "रोका जा रहा है…", "Deteniendo…", "Arrêt en cours…", "جارٍ الإيقاف…",
            "থামানো হচ্ছে…", "A parar…", "Остановка…", "روکا جا رہا ہے…", "Zatrzymywanie…", "Đang dừng…",
            "กำลังหยุด…"),

        ["BtnAddToQueue"] = L(
            "Add to queue", "加入队列", "कतार में जोड़ें", "Añadir a la cola", "Ajouter à la file",
            "إضافة إلى قائمة الانتظار", "সারিতে যোগ করুন", "Adicionar à fila", "Добавить в очередь",
            "قطار میں شامل کریں", "Dodaj do kolejki", "Thêm vào hàng đợi", "เพิ่มลงคิว"),

        ["BtnClearQueue"] = L(
            "Clear queue", "清空队列", "कतार साफ़ करें", "Vaciar la cola", "Vider la file",
            "مسح قائمة الانتظار", "সারি পরিষ্কার করুন", "Limpar fila", "Очистить очередь",
            "قطار صاف کریں", "Wyczyść kolejkę", "Xóa hàng đợi", "ล้างคิว"),

        ["QueuedLabel"] = L(
            "Queued: {0}", "已排队：{0}", "कतार में: {0}", "En cola: {0}", "En file : {0}",
            "في الانتظار: {0}", "সারিতে: {0}", "Em fila: {0}", "В очереди: {0}", "قطار میں: {0}",
            "W kolejce: {0}", "Trong hàng đợi: {0}", "อยู่ในคิว: {0}"),

        ["LanguageButtonTooltip"] = L(
            "Change language", "更改语言", "भाषा बदलें", "Cambiar idioma", "Changer de langue",
            "تغيير اللغة", "ভাষা পরিবর্তন করুন", "Mudar idioma", "Сменить язык", "زبان تبدیل کریں",
            "Zmień język", "Đổi ngôn ngữ", "เปลี่ยนภาษา"),

        ["NoFilesTitle"] = L(
            "No files", "没有文件", "कोई फ़ाइल नहीं", "Sin archivos", "Aucun fichier", "لا توجد ملفات",
            "কোনো ফাইল নেই", "Sem ficheiros", "Нет файлов", "کوئی فائل نہیں", "Brak plików",
            "Không có tệp", "ไม่มีไฟล์"),

        ["NoFilesAddOneMessage"] = L(
            "Add at least one video file.", "请至少添加一个视频文件。", "कृपया कम से कम एक वीडियो फ़ाइल जोड़ें।",
            "Añade al menos un archivo de vídeo.", "Ajoutez au moins un fichier vidéo.",
            "أضف ملف فيديو واحداً على الأقل.", "অন্তত একটি ভিডিও ফাইল যোগ করুন।",
            "Adicione pelo menos um ficheiro de vídeo.", "Добавьте хотя бы один видеофайл.",
            "کم از کم ایک ویڈیو فائل شامل کریں۔", "Dodaj co najmniej jeden plik wideo.",
            "Hãy thêm ít nhất một tệp video.", "เพิ่มไฟล์วิดีโออย่างน้อยหนึ่งไฟล์"),

        ["NoFilesFirstMessage"] = L(
            "Add at least one video file first.", "请先至少添加一个视频文件。",
            "पहले कम से कम एक वीडियो फ़ाइल जोड़ें।", "Primero añade al menos un archivo de vídeo.",
            "Ajoutez d'abord au moins un fichier vidéo.", "أضف أولاً ملف فيديو واحداً على الأقل.",
            "প্রথমে অন্তত একটি ভিডিও ফাইল যোগ করুন।", "Primeiro adicione pelo menos um ficheiro de vídeo.",
            "Сначала добавьте хотя бы один видеофайл.", "پہلے کم از کم ایک ویڈیو فائل شامل کریں۔",
            "Najpierw dodaj co najmniej jeden plik wideo.", "Hãy thêm ít nhất một tệp video trước.",
            "เพิ่มไฟล์วิดีโออย่างน้อยหนึ่งไฟล์ก่อน"),

        ["MissingRangesTitle"] = L(
            "Missing ranges", "缺少范围", "रेंज गायब है", "Faltan rangos", "Plages manquantes",
            "نطاقات مفقودة", "রেঞ্জ অনুপস্থিত", "Intervalos em falta", "Не указаны диапазоны",
            "رینج غائب ہے", "Brak zakresów", "Thiếu khoảng thời gian", "ไม่มีช่วงเวลา"),

        ["MissingRangesMessage"] = L(
            "Enter time ranges for file:\n{0}", "请为文件输入时间范围：\n{0}",
            "फ़ाइल के लिए समय अंतराल दर्ज करें:\n{0}", "Introduce los rangos de tiempo para el archivo:\n{0}",
            "Saisissez les plages horaires pour le fichier :\n{0}", "أدخل النطاقات الزمنية للملف:\n{0}",
            "ফাইলের জন্য সময়সীমা লিখুন:\n{0}", "Indique os intervalos de tempo para o ficheiro:\n{0}",
            "Укажите диапазоны времени для файла:\n{0}", "فائل کے لیے وقت کی حد درج کریں:\n{0}",
            "Podaj zakresy czasu dla pliku:\n{0}", "Nhập khoảng thời gian cho tệp:\n{0}",
            "ป้อนช่วงเวลาสำหรับไฟล์:\n{0}"),

        ["MissingRangesAtLeastOneMessage"] = L(
            "Enter at least one range for file:\n{0}", "请为文件至少输入一个范围：\n{0}",
            "फ़ाइल के लिए कम से कम एक रेंज दर्ज करें:\n{0}", "Introduce al menos un rango para el archivo:\n{0}",
            "Saisissez au moins une plage pour le fichier :\n{0}", "أدخل نطاقاً واحداً على الأقل للملف:\n{0}",
            "ফাইলের জন্য অন্তত একটি রেঞ্জ লিখুন:\n{0}", "Indique pelo menos um intervalo para o ficheiro:\n{0}",
            "Укажите хотя бы один диапазон для файла:\n{0}", "فائل کے لیے کم از کم ایک رینج درج کریں:\n{0}",
            "Podaj co najmniej jeden zakres dla pliku:\n{0}", "Nhập ít nhất một khoảng cho tệp:\n{0}",
            "ป้อนช่วงเวลาอย่างน้อยหนึ่งช่วงสำหรับไฟล์:\n{0}"),

        ["InvalidFormatTitle"] = L(
            "Invalid format", "格式无效", "अमान्य प्रारूप", "Formato no válido", "Format invalide",
            "تنسيق غير صالح", "অবৈধ ফরম্যাট", "Formato inválido", "Неверный формат", "غلط فارمیٹ",
            "Nieprawidłowy format", "Định dạng không hợp lệ", "รูปแบบไม่ถูกต้อง"),

        ["InvalidFormatMessage"] = L(
            "Invalid range '{0}' in file {1}", "文件 {1} 中的范围“{0}”无效", "फ़ाइल {1} में अमान्य रेंज '{0}'",
            "Rango no válido «{0}» en el archivo {1}", "Plage invalide « {0} » dans le fichier {1}",
            "نطاق غير صالح '{0}' في الملف {1}", "ফাইল {1}-এ অবৈধ রেঞ্জ '{0}'",
            "Intervalo inválido «{0}» no ficheiro {1}", "Недопустимый диапазон «{0}» в файле {1}",
            "فائل {1} میں غلط رینج '{0}'", "Nieprawidłowy zakres „{0}” w pliku {1}",
            "Khoảng không hợp lệ '{0}' trong tệp {1}", "ช่วงเวลาไม่ถูกต้อง '{0}' ในไฟล์ {1}"),

        ["InvalidTimeFormatTitle"] = L(
            "Invalid time format", "时间格式无效", "अमान्य समय प्रारूप", "Formato de hora no válido",
            "Format d'heure invalide", "تنسيق وقت غير صالح", "অবৈধ সময় ফরম্যাট", "Formato de hora inválido",
            "Неверный формат времени", "وقت کا غلط فارمیٹ", "Nieprawidłowy format czasu",
            "Định dạng thời gian không hợp lệ", "รูปแบบเวลาไม่ถูกต้อง"),

        ["InvalidRangeTitle"] = L(
            "Invalid range", "范围无效", "अमान्य रेंज", "Rango no válido", "Plage invalide",
            "نطاق غير صالح", "অবৈধ রেঞ্জ", "Intervalo inválido", "Недопустимый диапазон", "غلط رینج",
            "Nieprawidłowy zakres", "Khoảng không hợp lệ", "ช่วงเวลาไม่ถูกต้อง"),

        ["InvalidRangeMessage"] = L(
            "The end of a range must be after its start: {0}", "范围的结束时间必须晚于开始时间：{0}",
            "रेंज का अंत उसकी शुरुआत के बाद होना चाहिए: {0}", "El final de un rango debe ser posterior a su inicio: {0}",
            "La fin d'une plage doit être postérieure à son début : {0}", "يجب أن تكون نهاية النطاق بعد بدايته: {0}",
            "রেঞ্জের শেষ অবশ্যই শুরুর পরে হতে হবে: {0}", "O fim de um intervalo tem de ser posterior ao início: {0}",
            "Конец диапазона должен быть позже его начала: {0}", "رینج کا اختتام اس کے آغاز کے بعد ہونا چاہیے: {0}",
            "Koniec zakresu musi być późniejszy niż jego początek: {0}", "Điểm kết thúc của khoảng phải sau điểm bắt đầu: {0}",
            "จุดสิ้นสุดของช่วงต้องอยู่หลังจุดเริ่มต้น: {0}"),

        ["OverlappingRangesTitle"] = L(
            "Overlapping ranges", "范围重叠", "अतिव्यापी रेंज", "Rangos superpuestos", "Plages qui se chevauchent",
            "نطاقات متداخلة", "ওভারল্যাপিং রেঞ্জ", "Intervalos sobrepostos", "Пересекающиеся диапазоны",
            "اوورلیپنگ رینجز", "Nakładające się zakresy", "Các khoảng chồng chéo", "ช่วงเวลาที่ทับซ้อนกัน"),

        ["OverlappingRangesMessage"] = L(
            "Ranges {0} and {1} in {2} overlap - the shared part would end up twice in the output",
            "文件 {2} 中的范围 {0} 和 {1} 重叠 - 重叠部分会在输出中出现两次",
            "फ़ाइल {2} में रेंज {0} और {1} ओवरलैप करती हैं - साझा भाग आउटपुट में दो बार आ जाएगा",
            "Los rangos {0} y {1} en {2} se superponen: la parte compartida acabaría duplicada en la salida",
            "Les plages {0} et {1} dans {2} se chevauchent - la partie commune se retrouverait deux fois dans le résultat",
            "النطاقان {0} و{1} في {2} متداخلان - سينتهي الجزء المشترك مكررًا مرتين في الناتج",
            "{2}-এ {0} এবং {1} রেঞ্জ দুটি ওভারল্যাপ করছে - ভাগ করা অংশটি আউটপুটে দুইবার থাকবে",
            "Os intervalos {0} e {1} em {2} sobrepõem-se - a parte partilhada acabaria duplicada na saída",
            "Диапазоны {0} и {1} в {2} пересекаются - общая часть попадёт в результат дважды",
            "{2} میں رینجز {0} اور {1} اوورلیپ کرتی ہیں - مشترکہ حصہ آؤٹ پٹ میں دو بار آئے گا",
            "Zakresy {0} i {1} w {2} nakładają się na siebie - wspólna część trafiłaby do wyniku dwukrotnie",
            "Khoảng {0} và {1} trong {2} chồng lên nhau - phần chung sẽ xuất hiện hai lần trong kết quả",
            "ช่วงเวลา {0} และ {1} ใน {2} ทับซ้อนกัน - ส่วนที่ซ้อนทับจะปรากฏสองครั้งในผลลัพธ์"),

        ["OutputMatchesInputTitle"] = L(
            "Output matches a source file", "输出文件与源文件相同", "आउटपुट किसी स्रोत फ़ाइल से मेल खाता है",
            "El destino coincide con un archivo de origen", "La destination correspond à un fichier source",
            "الوجهة تطابق ملفًا مصدرًا", "আউটপুট একটি সোর্স ফাইলের সাথে মিলে যাচ্ছে",
            "O destino corresponde a um ficheiro de origem", "Файл назначения совпадает с исходным файлом",
            "آؤٹ پٹ کسی سورس فائل سے مماثل ہے", "Plik wyjściowy pokrywa się ze źródłowym",
            "Tệp đầu ra trùng với một tệp nguồn", "ไฟล์ผลลัพธ์ตรงกับไฟล์ต้นฉบับ"),

        ["OutputMatchesInputMessage"] = L(
            "'{0}' is one of the files being cut - choose a different output name so the source isn't overwritten",
            "“{0}”是正在剪辑的源文件之一 - 请选择其他输出文件名，以免覆盖源文件",
            "'{0}' उन फ़ाइलों में से एक है जिन्हें काटा जा रहा है - स्रोत को ओवरराइट होने से बचाने के लिए कोई और आउटपुट नाम चुनें",
            "«{0}» es uno de los archivos que se están recortando - elige otro nombre de salida para no sobrescribir el origen",
            "« {0} » fait partie des fichiers en cours de découpe - choisissez un autre nom de sortie pour ne pas écraser la source",
            "'{0}' هو أحد الملفات التي يتم قصها - اختر اسم ناتج مختلف حتى لا يتم استبدال المصدر",
            "'{0}' কাটা হচ্ছে এমন ফাইলগুলোর একটি - সোর্স ওভাররাইট এড়াতে অন্য আউটপুট নাম বেছে নিন",
            "«{0}» é um dos ficheiros a ser cortado - escolha outro nome de saída para não substituir a origem",
            "«{0}» — один из файлов, которые сейчас нарезаются - выберите другое имя для результата, чтобы не перезаписать исходник",
            "'{0}' ان فائلوں میں سے ایک ہے جنہیں کاٹا جا رہا ہے - سورس اوور رائٹ ہونے سے بچانے کے لیے مختلف آؤٹ پٹ نام منتخب کریں",
            "„{0}” to jeden z plików, które są właśnie cięte - wybierz inną nazwę wyjściową, żeby nie nadpisać źródła",
            "'{0}' là một trong các tệp đang được cắt - hãy chọn tên đầu ra khác để không ghi đè lên tệp gốc",
            "'{0}' เป็นหนึ่งในไฟล์ที่กำลังถูกตัด - โปรดเลือกชื่อไฟล์ผลลัพธ์อื่นเพื่อไม่ให้เขียนทับต้นฉบับ"),

        ["CouldNotSaveTitle"] = L(
            "Could not save project", "无法保存项目", "प्रोजेक्ट सहेजा नहीं जा सका", "No se pudo guardar el proyecto",
            "Impossible d'enregistrer le projet", "تعذر حفظ المشروع", "প্রকল্প সংরক্ষণ করা যায়নি",
            "Não foi possível guardar o projeto", "Не удалось сохранить проект", "پراجیکٹ محفوظ نہیں ہو سکا",
            "Nie udało się zapisać projektu", "Không thể lưu dự án", "ไม่สามารถบันทึกโปรเจกต์ได้"),

        ["CouldNotLoadTitle"] = L(
            "Could not load project", "无法加载项目", "प्रोजेक्ट लोड नहीं हो सका", "No se pudo cargar el proyecto",
            "Impossible de charger le projet", "تعذر تحميل المشروع", "প্রকল্প লোড করা যায়নি",
            "Não foi possível carregar o projeto", "Не удалось загрузить проект", "پراجیکٹ لوڈ نہیں ہو سکا",
            "Nie udało się wczytać projektu", "Không thể tải dự án", "ไม่สามารถโหลดโปรเจกต์ได้"),

        ["SomeFilesMissingTitle"] = L(
            "Some files are missing", "部分文件缺失", "कुछ फ़ाइलें गायब हैं", "Faltan algunos archivos",
            "Certains fichiers sont manquants", "بعض الملفات مفقودة", "কিছু ফাইল অনুপস্থিত",
            "Faltam alguns ficheiros", "Некоторые файлы отсутствуют", "کچھ فائلیں غائب ہیں",
            "Brakuje niektórych plików", "Thiếu một số tệp", "ไม่พบไฟล์บางไฟล์"),

        ["SomeFilesMissingMessage"] = L(
            "These files from the project could not be found and were skipped:\n\n{0}",
            "项目中的以下文件未找到，已跳过：\n\n{0}",
            "प्रोजेक्ट की ये फ़ाइलें नहीं मिलीं और छोड़ दी गईं:\n\n{0}",
            "No se encontraron estos archivos del proyecto y se omitieron:\n\n{0}",
            "Ces fichiers du projet sont introuvables et ont été ignorés :\n\n{0}",
            "تعذر العثور على هذه الملفات من المشروع وتم تخطيها:\n\n{0}",
            "প্রকল্পের এই ফাইলগুলো খুঁজে পাওয়া যায়নি এবং বাদ দেওয়া হয়েছে:\n\n{0}",
            "Estes ficheiros do projeto não foram encontrados e foram ignorados:\n\n{0}",
            "Эти файлы проекта не найдены и были пропущены:\n\n{0}",
            "پراجیکٹ کی یہ فائلیں نہیں ملیں اور نظر انداز کر دی گئیں:\n\n{0}",
            "Tych plików z projektu nie znaleziono i zostały pominięte:\n\n{0}",
            "Không tìm thấy các tệp sau của dự án nên đã bỏ qua:\n\n{0}",
            "ไม่พบไฟล์เหล่านี้จากโปรเจกต์และถูกข้ามไป:\n\n{0}"),

        ["ClearQueueTitle"] = L(
            "Clear queue", "清空队列", "कतार साफ़ करें", "Vaciar la cola", "Vider la file",
            "مسح قائمة الانتظار", "সারি পরিষ্কার করুন", "Limpar fila", "Очистить очередь",
            "قطار صاف کریں", "Wyczyść kolejkę", "Xóa hàng đợi", "ล้างคิว"),

        ["ClearQueueMessage"] = L(
            "Remove all {0} queued job(s)?", "要删除全部 {0} 个排队任务吗？", "सभी {0} कतारबद्ध कार्य हटाएं?",
            "¿Eliminar los {0} trabajo(s) en cola?", "Supprimer les {0} tâche(s) en file ?",
            "هل تريد إزالة جميع المهام قيد الانتظار وعددها {0}؟", "সারিতে থাকা {0}টি কাজ সব মুছে ফেলবেন?",
            "Remover todos os {0} trabalho(s) em fila?", "Удалить все задания в очереди ({0})?",
            "قطار میں موجود تمام {0} کام حذف کریں؟", "Usunąć wszystkie {0} zadania z kolejki?",
            "Xóa tất cả {0} tác vụ trong hàng đợi?", "ลบงานทั้งหมด {0} รายการในคิวหรือไม่?"),

        ["NothingToDoTitle"] = L(
            "Nothing to do", "无事可做", "करने के लिए कुछ नहीं", "Nada que hacer", "Rien à faire",
            "لا يوجد ما يمكن فعله", "করার কিছু নেই", "Nada a fazer", "Нечего делать",
            "کرنے کے لیے کچھ نہیں", "Nie ma nic do zrobienia", "Không có gì để làm", "ไม่มีอะไรให้ทำ"),

        ["NothingToDoMessage"] = L(
            "Add at least one video file, or queue a job first.", "请先添加至少一个视频文件，或先将任务加入队列。",
            "पहले कम से कम एक वीडियो फ़ाइल जोड़ें, या एक कार्य कतार में डालें।",
            "Añade al menos un archivo de vídeo, o pon primero un trabajo en la cola.",
            "Ajoutez au moins un fichier vidéo, ou mettez d'abord une tâche en file.",
            "أضف ملف فيديو واحداً على الأقل، أو أضف مهمة إلى قائمة الانتظار أولاً.",
            "অন্তত একটি ভিডিও ফাইল যোগ করুন, অথবা প্রথমে একটি কাজ সারিতে যোগ করুন।",
            "Adicione pelo menos um ficheiro de vídeo, ou coloque primeiro um trabalho na fila.",
            "Добавьте хотя бы один видеофайл или сначала поставьте задание в очередь.",
            "کم از کم ایک ویڈیو فائل شامل کریں، یا پہلے کوئی کام قطار میں ڈالیں۔",
            "Dodaj co najmniej jeden plik wideo albo najpierw dodaj zadanie do kolejki.",
            "Hãy thêm ít nhất một tệp video, hoặc thêm một tác vụ vào hàng đợi trước.",
            "เพิ่มไฟล์วิดีโออย่างน้อยหนึ่งไฟล์ หรือเพิ่มงานลงคิวก่อน"),

        ["StopAndQuitTitle"] = L(
            "Stop and quit?", "停止并退出？", "रोकें और बाहर निकलें?", "¿Detener y salir?", "Arrêter et quitter ?",
            "إيقاف والخروج؟", "থামিয়ে বেরিয়ে যাবেন?", "Parar e sair?", "Остановить и выйти?",
            "روک کر باہر نکلیں؟", "Zatrzymać i zamknąć?", "Dừng và thoát?", "หยุดและออกหรือไม่?"),

        ["StopAndQuitMessage"] = L(
            "A job is still running. Stop it and quit?", "有任务仍在运行。要停止并退出吗？",
            "एक कार्य अभी भी चल रहा है। इसे रोककर बाहर निकलें?", "Todavía hay un trabajo en curso. ¿Detenerlo y salir?",
            "Une tâche est toujours en cours. L'arrêter et quitter ?",
            "لا تزال هناك مهمة قيد التشغيل. هل تريد إيقافها والخروج؟", "একটি কাজ এখনও চলছে। থামিয়ে বেরিয়ে যাবেন?",
            "Ainda há um trabalho em curso. Pará-lo e sair?", "Задание всё ещё выполняется. Остановить его и выйти?",
            "ایک کام ابھی جاری ہے۔ اسے روک کر باہر نکلیں؟", "Zadanie wciąż trwa. Zatrzymać je i zamknąć aplikację?",
            "Vẫn còn một tác vụ đang chạy. Dừng nó và thoát?", "งานยังทำงานอยู่ ต้องการหยุดแล้วออกหรือไม่?"),

        ["StopProcessingTitle"] = L(
            "Stop processing?", "停止处理？", "प्रोसेसिंग रोकें?", "¿Detener el procesamiento?",
            "Arrêter le traitement ?", "إيقاف المعالجة؟", "প্রক্রিয়াকরণ থামাবেন?", "Parar o processamento?",
            "Остановить обработку?", "پروسیسنگ روکیں؟", "Zatrzymać przetwarzanie?", "Dừng xử lý?",
            "หยุดการประมวลผลหรือไม่?"),

        ["StopProcessingMessage"] = L(
            "The current job will be interrupted and its progress lost. Are you sure you want to stop?",
            "当前任务将被中断，进度将丢失。确定要停止吗？",
            "मौजूदा कार्य बाधित हो जाएगा और उसकी प्रगति खो जाएगी। क्या आप वाकई रोकना चाहते हैं?",
            "El trabajo actual se interrumpirá y se perderá su progreso. ¿Seguro que quieres detenerlo?",
            "La tâche en cours sera interrompue et sa progression sera perdue. Voulez-vous vraiment arrêter ?",
            "ستتم مقاطعة المهمة الحالية وسيُفقد تقدمها. هل أنت متأكد أنك تريد الإيقاف؟",
            "চলমান কাজটি বাধাগ্রস্ত হবে এবং অগ্রগতি হারিয়ে যাবে। আপনি কি নিশ্চিত থামাতে চান?",
            "O trabalho atual será interrompido e o seu progresso perdido. Tem a certeza de que quer parar?",
            "Текущее задание будет прервано, и прогресс будет потерян. Вы уверены, что хотите остановить?",
            "موجودہ کام میں خلل پڑے گا اور اس کی پیش رفت ضائع ہو جائے گی۔ کیا آپ واقعی روکنا چاہتے ہیں؟",
            "Bieżące zadanie zostanie przerwane, a jego postęp utracony. Czy na pewno chcesz zatrzymać?",
            "Tác vụ hiện tại sẽ bị gián đoạn và tiến trình sẽ mất. Bạn có chắc muốn dừng không?",
            "งานปัจจุบันจะถูกขัดจังหวะและความคืบหน้าจะสูญหาย คุณแน่ใจหรือไม่ว่าต้องการหยุด?"),

        ["SelectVideoFilesTitle"] = L(
            "Select one or more video files", "选择一个或多个视频文件", "एक या अधिक वीडियो फ़ाइलें चुनें",
            "Selecciona uno o más archivos de vídeo", "Sélectionnez un ou plusieurs fichiers vidéo",
            "حدد ملف فيديو واحداً أو أكثر", "একটি বা একাধিক ভিডিও ফাইল নির্বাচন করুন",
            "Selecione um ou mais ficheiros de vídeo", "Выберите один или несколько видеофайлов",
            "ایک یا زیادہ ویڈیو فائلیں منتخب کریں", "Wybierz jeden lub więcej plików wideo",
            "Chọn một hoặc nhiều tệp video", "เลือกไฟล์วิดีโอหนึ่งไฟล์ขึ้นไป"),

        ["SaveProjectAsTitle"] = L(
            "Save project as", "项目另存为", "प्रोजेक्ट इस रूप में सहेजें", "Guardar proyecto como",
            "Enregistrer le projet sous", "حفظ المشروع باسم", "প্রকল্প এভাবে সংরক্ষণ করুন",
            "Guardar projeto como", "Сохранить проект как", "پراجیکٹ اس نام سے محفوظ کریں",
            "Zapisz projekt jako", "Lưu dự án dưới dạng", "บันทึกโปรเจกต์เป็น"),

        ["LoadProjectTitle"] = L(
            "Load project", "加载项目", "प्रोजेक्ट लोड करें", "Cargar proyecto", "Charger le projet",
            "تحميل المشروع", "প্রকল্প লোড করুন", "Carregar projeto", "Загрузить проект",
            "پراجیکٹ لوڈ کریں", "Wczytaj projekt", "Tải dự án", "โหลดโปรเจกต์"),

        ["SaveOutputAsTitle"] = L(
            "Save output file as", "输出文件另存为", "आउटपुट फ़ाइल इस रूप में सहेजें",
            "Guardar archivo de salida como", "Enregistrer le fichier de sortie sous",
            "حفظ ملف الإخراج باسم", "আউটপুট ফাইল এভাবে সংরক্ষণ করুন", "Guardar ficheiro de saída como",
            "Сохранить выходной файл как", "آؤٹ پٹ فائل اس نام سے محفوظ کریں",
            "Zapisz plik wyjściowy jako", "Lưu tệp đầu ra dưới dạng", "บันทึกไฟล์ผลลัพธ์เป็น"),

        ["Cancel"] = L(
            "Cancel", "取消", "रद्द करें", "Cancelar", "Annuler", "إلغاء", "বাতিল", "Cancelar",
            "Отмена", "منسوخ کریں", "Anuluj", "Hủy", "ยกเลิก"),

        ["OK"] = L(
            "OK", "确定", "ठीक है", "Aceptar", "OK", "موافق", "ঠিক আছে", "OK", "ОК", "ٹھیک ہے",
            "OK", "OK", "ตกลง"),

        ["Yes"] = L(
            "Yes", "是", "हाँ", "Sí", "Oui", "نعم", "হ্যাঁ", "Sim", "Да", "ہاں",
            "Tak", "Có", "ใช่"),

        ["No"] = L(
            "No", "否", "नहीं", "No", "Non", "لا", "না", "Não", "Нет", "نہیں",
            "Nie", "Không", "ไม่"),

        ["DoneTitle"] = L(
            "Done", "完成", "पूर्ण", "Hecho", "Terminé", "تم", "সম্পন্ন", "Concluído", "Готово",
            "مکمل", "Gotowe", "Xong", "เสร็จสิ้น"),

        ["OutputFileSaved"] = L(
            "Output file saved:", "输出文件已保存：", "आउटपुट फ़ाइल सहेजी गई:", "Archivo de salida guardado:",
            "Fichier de sortie enregistré :", "تم حفظ ملف الإخراج:", "আউটপুট ফাইল সংরক্ষিত হয়েছে:",
            "Ficheiro de saída guardado:", "Выходной файл сохранён:", "آؤٹ پٹ فائل محفوظ ہو گئی:",
            "Zapisano plik wyjściowy:", "Đã lưu tệp đầu ra:", "บันทึกไฟล์ผลลัพธ์แล้ว:"),

        ["DragToReorderTooltip"] = L(
            "Drag to reorder", "拖动以重新排序", "क्रम बदलने के लिए खींचें", "Arrastra para reordenar",
            "Faites glisser pour réorganiser", "اسحب لإعادة الترتيب", "ক্রম পরিবর্তন করতে টেনে আনুন",
            "Arraste para reordenar", "Перетащите, чтобы изменить порядок", "ترتیب بدلنے کے لیے گھسیٹیں",
            "Przeciągnij, aby zmienić kolejność", "Kéo để sắp xếp lại", "ลากเพื่อจัดเรียงใหม่"),

        ["PickRangesTooltip"] = L(
            "Pick ranges visually on a timeline", "在时间轴上直观选择范围", "टाइमलाइन पर देखकर रेंज चुनें",
            "Elige los rangos visualmente en una línea de tiempo", "Choisissez les plages visuellement sur une chronologie",
            "اختر النطاقات بصرياً على الخط الزمني", "টাইমলাইনে দৃশ্যত রেঞ্জ বাছাই করুন",
            "Escolha os intervalos visualmente numa linha do tempo", "Выберите диапазоны наглядно на шкале времени",
            "ٹائم لائن پر بصری طور پر رینج منتخب کریں", "Wybierz zakresy wizualnie na osi czasu",
            "Chọn khoảng thời gian trực quan trên dòng thời gian", "เลือกช่วงเวลาแบบเห็นภาพบนไทม์ไลน์"),

        ["RemoveFileTooltip"] = L(
            "Remove this file from the list", "从列表中移除此文件", "इस फ़ाइल को सूची से हटाएं",
            "Quitar este archivo de la lista", "Retirer ce fichier de la liste", "إزالة هذا الملف من القائمة",
            "তালিকা থেকে এই ফাইলটি সরান", "Remover este ficheiro da lista", "Удалить этот файл из списка",
            "اس فائل کو فہرست سے ہٹائیں", "Usuń ten plik z listy", "Xóa tệp này khỏi danh sách",
            "ลบไฟล์นี้ออกจากรายการ"),

        ["ErrorTitle"] = L(
            "Error", "错误", "त्रुटि", "Error", "Erreur", "خطأ", "ত্রুটি", "Erro", "Ошибка", "خرابی",
            "Błąd", "Lỗi", "ข้อผิดพลาด"),

        ["OutputFilesSaved"] = L(
            "Output files saved:", "输出文件已保存：", "आउटपुट फ़ाइलें सहेजी गईं:", "Archivos de salida guardados:",
            "Fichiers de sortie enregistrés :", "تم حفظ ملفات الإخراج:", "আউটপুট ফাইলগুলো সংরক্ষিত হয়েছে:",
            "Ficheiros de saída guardados:", "Выходные файлы сохранены:", "آؤٹ پٹ فائلیں محفوظ ہو گئیں:",
            "Zapisano pliki wyjściowe:", "Đã lưu các tệp đầu ra:", "บันทึกไฟล์ผลลัพธ์แล้ว:"),

        // -- Views/PreviewPlayerWindow.cs ("Trim range" window) ------------------

        ["TrimRangeTitle"] = L(
            "Trim range - {0}", "剪辑范围 - {0}", "रेंज ट्रिम करें - {0}", "Recortar rango - {0}",
            "Ajuster la plage - {0}", "قص النطاق - {0}", "রেঞ্জ ছাঁটাই করুন - {0}", "Recortar intervalo - {0}",
            "Обрезка диапазона - {0}", "رینج تراشیں - {0}", "Przytnij zakres - {0}", "Cắt khoảng - {0}",
            "ตัดช่วงเวลา - {0}"),

        ["CouldNotOpenFileTitle"] = L(
            "Could not open file", "无法打开文件", "फ़ाइल नहीं खोली जा सकी", "No se pudo abrir el archivo",
            "Impossible d'ouvrir le fichier", "تعذر فتح الملف", "ফাইল খোলা যায়নি",
            "Não foi possível abrir o ficheiro", "Не удалось открыть файл", "فائل نہیں کھل سکی",
            "Nie udało się otworzyć pliku", "Không thể mở tệp", "ไม่สามารถเปิดไฟล์ได้"),

        ["FailedToReadVideoInfoMessage"] = L(
            "Failed to read video info:\n{0}", "无法读取视频信息：\n{0}", "वीडियो जानकारी नहीं पढ़ी जा सकी:\n{0}",
            "No se pudo leer la información del vídeo:\n{0}", "Impossible de lire les informations vidéo :\n{0}",
            "تعذرت قراءة معلومات الفيديو:\n{0}", "ভিডিও তথ্য পড়া যায়নি:\n{0}",
            "Não foi possível ler as informações do vídeo:\n{0}", "Не удалось прочитать сведения о видео:\n{0}",
            "ویڈیو کی معلومات نہیں پڑھی جا سکیں:\n{0}", "Nie udało się odczytać informacji o wideo:\n{0}",
            "Không thể đọc thông tin video:\n{0}", "ไม่สามารถอ่านข้อมูลวิดีโอได้:\n{0}"),

        ["NoDurationMessage"] = L(
            "This file reports no duration - it can't be previewed.", "此文件未报告时长——无法预览。",
            "इस फ़ाइल की कोई अवधि नहीं मिली - इसका पूर्वावलोकन नहीं किया जा सकता।",
            "Este archivo no indica duración: no se puede previsualizar.",
            "Ce fichier n'indique aucune durée - impossible de le prévisualiser.",
            "لا يُبلغ هذا الملف عن أي مدة - لا يمكن معاينته.",
            "এই ফাইলের কোনো সময়কাল পাওয়া যায়নি - এটি পূর্বরূপে দেখা যাবে না।",
            "Este ficheiro não indica duração - não pode ser pré-visualizado.",
            "Этот файл не сообщает длительность - его нельзя предпросмотреть.",
            "اس فائل کا کوئی دورانیہ نہیں ملا - اس کا پیش منظر ممکن نہیں۔",
            "Ten plik nie podaje czasu trwania - nie można go podejrzeć.",
            "Tệp này không báo cáo thời lượng - không thể xem trước.", "ไฟล์นี้ไม่รายงานความยาว - ไม่สามารถดูตัวอย่างได้"),

        ["MuteTooltip"] = L(
            "Mute", "静音", "म्यूट करें", "Silenciar", "Couper le son", "كتم الصوت", "নিঃশব্দ করুন", "Silenciar",
            "Выключить звук", "خاموش کریں", "Wycisz", "Tắt tiếng", "ปิดเสียง"),

        ["UnmuteTooltip"] = L(
            "Unmute", "取消静音", "अनम्यूट करें", "Activar sonido", "Réactiver le son", "إلغاء كتم الصوت",
            "শব্দ চালু করুন", "Ativar som", "Включить звук", "آواز بحال کریں", "Wyłącz wyciszenie", "Bật tiếng",
            "เปิดเสียง"),

        ["FullClipOverviewLabel"] = L(
            "FULL CLIP OVERVIEW", "完整片段概览", "पूरी क्लिप का अवलोकन", "VISTA GENERAL DEL CLIP COMPLETO",
            "APERÇU DU CLIP COMPLET", "نظرة عامة على المقطع الكامل", "সম্পূর্ণ ক্লিপের সারসংক্ষেপ",
            "VISÃO GERAL DO CLIPE COMPLETO", "ОБЗОР ВСЕГО РОЛИКА", "مکمل کلپ کا جائزہ", "PODGLĄD CAŁEGO KLIPU",
            "TỔNG QUAN TOÀN BỘ ĐOẠN", "ภาพรวมคลิปทั้งหมด"),

        ["ZoomOutTooltip"] = L(
            "Zoom out", "缩小", "ज़ूम आउट करें", "Alejar", "Zoom arrière", "تصغير", "জুম আউট", "Reduzir zoom",
            "Уменьшить масштаб", "زوم آؤٹ کریں", "Pomniejsz", "Thu nhỏ", "ซูมออก"),

        ["ZoomInTooltip"] = L(
            "Zoom in", "放大", "ज़ूम इन करें", "Acercar", "Zoom avant", "تكبير", "জুম ইন", "Aumentar zoom",
            "Увеличить масштаб", "زوم اِن کریں", "Powiększ", "Phóng to", "ซูมเข้า"),

        ["FitLabel"] = L(
            "Fit", "适应", "फ़िट करें", "Ajustar", "Ajuster", "ملاءمة", "ফিট করুন", "Ajustar", "По размеру",
            "فٹ کریں", "Dopasuj", "Vừa khít", "พอดี"),

        ["FitTooltip"] = L(
            "Show the whole file", "显示整个文件", "पूरी फ़ाइल दिखाएं", "Mostrar todo el archivo",
            "Afficher tout le fichier", "عرض الملف بالكامل", "পুরো ফাইল দেখান", "Mostrar o ficheiro completo",
            "Показать весь файл", "پوری فائل دکھائیں", "Pokaż cały plik", "Hiển thị toàn bộ tệp",
            "แสดงไฟล์ทั้งหมด"),

        ["MarkInLabel"] = L(
            "Mark in · I", "标记入点 · I", "इन मार्क करें · I", "Marcar entrada · I", "Marquer l'entrée · I",
            "وضع علامة البداية · I", "শুরু চিহ্নিত করুন · I", "Marcar entrada · I", "Отметить начало · I",
            "شروع نشان زد کریں · I", "Zaznacz początek · I", "Đánh dấu vào · I", "ทำเครื่องหมายเข้า · I"),

        ["MarkInTooltip"] = L(
            "Set Start to the playhead (I)", "将开始点设为播放头位置 (I)", "प्लेहेड पर स्टार्ट सेट करें (I)",
            "Establece el inicio en la posición actual (I)", "Définit le début sur la tête de lecture (I)",
            "تعيين البداية عند رأس التشغيل (I)", "প্লেহেডে স্টার্ট সেট করুন (I)",
            "Define o início na posição atual (I)", "Установить начало на позиции воспроизведения (I)",
            "پلے ہیڈ پر آغاز مقرر کریں (I)", "Ustawia początek na pozycji odtwarzania (I)",
            "Đặt điểm Bắt đầu tại vị trí phát (I)", "ตั้งจุดเริ่มต้นที่ตำแหน่งเล่น (I)"),

        ["PreviousFrameTooltip"] = L(
            "Previous frame", "上一帧", "पिछला फ़्रेम", "Fotograma anterior", "Image précédente", "الإطار السابق",
            "পূর্ববর্তী ফ্রেম", "Fotograma anterior", "Предыдущий кадр", "پچھلا فریم", "Poprzednia klatka",
            "Khung hình trước", "เฟรมก่อนหน้า"),

        ["NextFrameTooltip"] = L(
            "Next frame", "下一帧", "अगला फ़्रेम", "Fotograma siguiente", "Image suivante", "الإطار التالي",
            "পরবর্তী ফ্রেম", "Fotograma seguinte", "Следующий кадр", "اگلا فریم", "Następna klatka",
            "Khung hình sau", "เฟรมถัดไป"),

        ["MarkOutLabel"] = L(
            "O · Mark out", "O · 标记出点", "O · आउट मार्क करें", "O · Marcar salida", "O · Marquer la sortie",
            "O · وضع علامة النهاية", "O · শেষ চিহ্নিত করুন", "O · Marcar saída", "O · Отметить конец",
            "O · اختتام نشان زد کریں", "O · Zaznacz koniec", "O · Đánh dấu ra", "O · ทำเครื่องหมายออก"),

        ["MarkOutTooltip"] = L(
            "Set End to the playhead (O)", "将结束点设为播放头位置 (O)", "प्लेहेड पर एंड सेट करें (O)",
            "Establece el final en la posición actual (O)", "Définit la fin sur la tête de lecture (O)",
            "تعيين النهاية عند رأس التشغيل (O)", "প্লেহেডে এন্ড সেট করুন (O)",
            "Define o fim na posição atual (O)", "Установить конец на позиции воспроизведения (O)",
            "پلے ہیڈ پر اختتام مقرر کریں (O)", "Ustawia koniec na pozycji odtwarzania (O)",
            "Đặt điểm Kết thúc tại vị trí phát (O)", "ตั้งจุดสิ้นสุดที่ตำแหน่งเล่น (O)"),

        ["FieldStartLabel"] = L(
            "START", "开始", "आरंभ", "INICIO", "DÉBUT", "البداية", "শুরু", "INÍCIO", "НАЧАЛО", "آغاز",
            "POCZĄTEK", "BẮT ĐẦU", "เริ่ม"),

        ["FieldEndLabel"] = L(
            "END", "结束", "अंत", "FIN", "FIN", "النهاية", "শেষ", "FIM", "КОНЕЦ", "اختتام", "KONIEC",
            "KẾT THÚC", "สิ้นสุด"),

        ["FieldLengthLabel"] = L(
            "LENGTH", "时长", "अवधि", "DURACIÓN", "DURÉE", "المدة", "দৈর্ঘ্য", "DURAÇÃO", "ДЛИТЕЛЬНОСТЬ",
            "دورانیہ", "DŁUGOŚĆ", "ĐỘ DÀI", "ความยาว"),

        ["SnapToKeyframesLabel"] = L(
            "Snap to keyframes", "吸附到关键帧", "कीफ़्रेम पर स्नैप करें", "Ajustar a fotogramas clave",
            "Aligner sur les images clés", "الالتصاق بالإطارات الرئيسية", "কীফ্রেমে স্ন্যাপ করুন",
            "Ajustar aos fotogramas-chave", "Привязка к ключевым кадрам", "کی فریمز پر سنیپ کریں",
            "Przyciągaj do klatek kluczowych", "Hút vào khung hình khóa", "ดึงเข้าคีย์เฟรม"),

        ["ReadingKeyframesTooltip"] = L(
            "Reading keyframes...", "正在读取关键帧…", "कीफ़्रेम पढ़े जा रहे हैं…", "Leyendo fotogramas clave…",
            "Lecture des images clés…", "جارٍ قراءة الإطارات الرئيسية…", "কীফ্রেম পড়া হচ্ছে…",
            "A ler fotogramas-chave…", "Чтение ключевых кадров…", "کی فریمز پڑھے جا رہے ہیں…",
            "Odczytywanie klatek kluczowych…", "Đang đọc khung hình khóa…", "กำลังอ่านคีย์เฟรม…"),

        ["NoKeyframesTooltip"] = L(
            "No keyframe list could be read for this file", "无法读取此文件的关键帧列表",
            "इस फ़ाइल के लिए कीफ़्रेम सूची नहीं पढ़ी जा सकी",
            "No se pudo leer la lista de fotogramas clave de este archivo",
            "Impossible de lire la liste des images clés de ce fichier",
            "تعذرت قراءة قائمة الإطارات الرئيسية لهذا الملف", "এই ফাইলের কীফ্রেম তালিকা পড়া যায়নি",
            "Não foi possível ler a lista de fotogramas-chave deste ficheiro",
            "Не удалось прочитать список ключевых кадров для этого файла",
            "اس فائل کی کی فریم فہرست نہیں پڑھی جا سکی",
            "Nie udało się odczytać listy klatek kluczowych dla tego pliku",
            "Không thể đọc danh sách khung hình khóa cho tệp này", "ไม่สามารถอ่านรายการคีย์เฟรมของไฟล์นี้ได้"),

        ["SnapTooltip"] = L(
            "Pull the range edges onto the nearest keyframe", "将范围边缘吸附到最近的关键帧",
            "रेंज के किनारों को निकटतम कीफ़्रेम पर खींचें", "Ajusta los bordes del rango al fotograma clave más cercano",
            "Aligne les bords de la plage sur l'image clé la plus proche",
            "سحب حواف النطاق إلى أقرب إطار رئيسي", "রেঞ্জের প্রান্তগুলো নিকটতম কীফ্রেমে টেনে নিন",
            "Ajusta os limites do intervalo ao fotograma-chave mais próximo",
            "Привязывает края диапазона к ближайшему ключевому кадру",
            "رینج کے کناروں کو قریب ترین کی فریم پر کھینچیں",
            "Przyciąga krawędzie zakresu do najbliższej klatki kluczowej",
            "Kéo các cạnh của khoảng vào khung hình khóa gần nhất", "ดึงขอบช่วงเวลาไปยังคีย์เฟรมที่ใกล้ที่สุด"),

        ["AddRangeLabel"] = L(
            "Add range", "添加范围", "रेंज जोड़ें", "Añadir rango", "Ajouter une plage", "إضافة نطاق",
            "রেঞ্জ যোগ করুন", "Adicionar intervalo", "Добавить диапазон", "رینج شامل کریں", "Dodaj zakres",
            "Thêm khoảng", "เพิ่มช่วงเวลา"),

        ["AddRangeTooltip"] = L(
            "Close this range off and start a new one after it", "结束此范围并在其后开始新范围",
            "इस रेंज को बंद करें और इसके बाद एक नई रेंज शुरू करें",
            "Cierra este rango y comienza uno nuevo a continuación",
            "Ferme cette plage et en commence une nouvelle juste après",
            "إغلاق هذا النطاق وبدء نطاق جديد بعده", "এই রেঞ্জ বন্ধ করে এর পরে একটি নতুন রেঞ্জ শুরু করুন",
            "Fecha este intervalo e inicia um novo a seguir",
            "Закрывает этот диапазон и начинает новый сразу после него",
            "اس رینج کو بند کریں اور اس کے بعد نئی رینج شروع کریں",
            "Zamyka ten zakres i zaczyna nowy zaraz po nim",
            "Đóng khoảng này lại và bắt đầu một khoảng mới ngay sau đó", "ปิดช่วงนี้และเริ่มช่วงใหม่ต่อจากนี้"),

        ["MagnetTooltip"] = L(
            "On a keyframe - this edge can be cut without re-encoding", "位于关键帧上——此边缘可无需重新编码即可剪切",
            "कीफ़्रेम पर - इस किनारे को बिना दोबारा एन्कोड किए काटा जा सकता है",
            "Sobre un fotograma clave: este borde se puede cortar sin recodificar",
            "Sur une image clé - ce bord peut être coupé sans réencodage",
            "على إطار رئيسي - يمكن قص هذه الحافة دون إعادة الترميز",
            "কীফ্রেমে আছে - এই প্রান্তটি পুনরায় এনকোড না করেই কাটা যাবে",
            "Num fotograma-chave - este limite pode ser cortado sem recodificar",
            "На ключевом кадре - этот край можно обрезать без перекодирования",
            "کی فریم پر - اس کنارے کو دوبارہ انکوڈ کیے بغیر کاٹا جا سکتا ہے",
            "Na klatce kluczowej - tę krawędź można wyciąć bez ponownego kodowania",
            "Nằm trên khung hình khóa - có thể cắt cạnh này mà không cần mã hóa lại",
            "อยู่บนคีย์เฟรม - สามารถตัดขอบนี้ได้โดยไม่ต้องเข้ารหัสใหม่"),

        ["TrimHintText"] = L(
            "Drag handles, or press I / O to mark in/out at the playhead. Scroll to zoom the track below.",
            "拖动手柄，或按 I / O 在播放头处标记入点/出点。滚动可缩放下方轨道。",
            "हैंडल खींचें, या प्लेहेड पर इन/आउट मार्क करने के लिए I / O दबाएं। नीचे ट्रैक को ज़ूम करने के लिए स्क्रॉल करें।",
            "Arrastra los tiradores, o pulsa I / O para marcar entrada/salida en la posición actual. Desplázate para hacer zoom en la pista de abajo.",
            "Faites glisser les poignées, ou appuyez sur I / O pour marquer l'entrée/la sortie à la tête de lecture. Faites défiler pour zoomer sur la piste ci-dessous.",
            "اسحب المقابض، أو اضغط I / O لوضع علامة البداية/النهاية عند رأس التشغيل. مرّر للتكبير/التصغير في المسار أدناه.",
            "হ্যান্ডেল টেনে আনুন, বা প্লেহেডে ইন/আউট চিহ্নিত করতে I / O চাপুন। নিচের ট্র্যাক জুম করতে স্ক্রল করুন।",
            "Arraste as pegas, ou prima I / O para marcar entrada/saída na posição atual. Percorra para ampliar a faixa abaixo.",
            "Перетаскивайте маркеры или нажимайте I / O, чтобы отметить начало/конец на позиции воспроизведения. Прокрутите для масштабирования дорожки ниже.",
            "ہینڈلز کھینچیں، یا پلے ہیڈ پر ان/آؤٹ نشان زد کرنے کے لیے I / O دبائیں۔ نیچے ٹریک زوم کرنے کے لیے سکرول کریں۔",
            "Przeciągaj uchwyty albo naciśnij I / O, aby zaznaczyć początek/koniec na pozycji odtwarzania. Przewiń, aby powiększyć oś poniżej.",
            "Kéo các tay cầm, hoặc nhấn I / O để đánh dấu vào/ra tại vị trí phát. Cuộn để phóng to dải bên dưới.",
            "ลากที่จับ หรือกด I / O เพื่อทำเครื่องหมายเข้า/ออกที่ตำแหน่งเล่น เลื่อนเพื่อซูมแทร็กด้านล่าง"),

        ["ApplyLabel"] = L(
            "Apply", "应用", "लागू करें", "Aplicar", "Appliquer", "تطبيق", "প্রয়োগ করুন", "Aplicar", "Применить",
            "لاگو کریں", "Zastosuj", "Áp dụng", "นำไปใช้"),

        ["PauseTooltip"] = L(
            "Pause", "暂停", "रोकें", "Pausar", "Pause", "إيقاف مؤقت", "বিরতি", "Pausar", "Пауза", "توقف",
            "Pauza", "Tạm dừng", "หยุดชั่วคราว"),

        ["PlayTooltip"] = L(
            "Play", "播放", "चलाएं", "Reproducir", "Lire", "تشغيل", "চালান", "Reproduzir", "Воспроизвести",
            "چلائیں", "Odtwórz", "Phát", "เล่น"),

        ["PlaybackUnavailableTooltip"] = L(
            "Playback unavailable - Windows has no decoder for this file. Scrubbing still works.",
            "无法播放——Windows 没有此文件的解码器。仍可拖动预览。",
            "प्लेबैक उपलब्ध नहीं - Windows के पास इस फ़ाइल के लिए डिकोडर नहीं है। स्क्रबिंग अभी भी काम करती है।",
            "Reproducción no disponible: Windows no tiene un decodificador para este archivo. El desplazamiento sigue funcionando.",
            "Lecture indisponible - Windows n'a pas de décodeur pour ce fichier. Le défilement fonctionne toujours.",
            "التشغيل غير متاح - لا يملك Windows مفكِّك ترميز لهذا الملف. لا يزال التمرير يعمل.",
            "প্লেব্যাক উপলব্ধ নয় - Windows-এর কাছে এই ফাইলের ডিকোডার নেই। স্ক্রাবিং তবুও কাজ করে।",
            "Reprodução indisponível - o Windows não tem um descodificador para este ficheiro. A navegação continua a funcionar.",
            "Воспроизведение недоступно - у Windows нет декодера для этого файла. Перемотка всё ещё работает.",
            "چلانا دستیاب نہیں - Windows کے پاس اس فائل کے لیے ڈی کوڈر نہیں ہے۔ سکربنگ اب بھی کام کرتی ہے۔",
            "Odtwarzanie niedostępne - system Windows nie ma dekodera dla tego pliku. Przewijanie nadal działa.",
            "Không thể phát - Windows không có bộ giải mã cho tệp này. Việc tua vẫn hoạt động.",
            "เล่นไม่ได้ - Windows ไม่มีตัวถอดรหัสสำหรับไฟล์นี้ แต่การเลื่อนดูยังใช้งานได้"),

        ["RangesCountHeader"] = L(
            "Ranges · {0}", "范围 · {0}", "रेंज · {0}", "Rangos · {0}", "Plages · {0}", "النطاقات · {0}",
            "রেঞ্জ · {0}", "Intervalos · {0}", "Диапазоны · {0}", "رینجز · {0}", "Zakresy · {0}", "Khoảng · {0}",
            "ช่วงเวลา · {0}"),

        ["TotalSuffix"] = L(
            "{0} total", "共 {0}", "कुल {0}", "{0} en total", "{0} au total", "الإجمالي {0}", "মোট {0}",
            "{0} no total", "всего {0}", "کل {0}", "razem {0}", "tổng {0}", "รวม {0}"),

        ["RangeSummarySingular"] = L(
            "1 range selected · {0} total", "已选择 1 个范围 · 共 {0}", "1 रेंज चुनी गई · कुल {0}",
            "1 rango seleccionado · {0} en total", "1 plage sélectionnée · {0} au total",
            "تم تحديد نطاق واحد · الإجمالي {0}", "১টি রেঞ্জ নির্বাচিত · মোট {0}",
            "1 intervalo selecionado · {0} no total", "Выбран 1 диапазон · всего {0}", "1 رینج منتخب · کل {0}",
            "Wybrano 1 zakres · razem {0}", "Đã chọn 1 khoảng · tổng {0}", "เลือก 1 ช่วงเวลา · รวม {0}"),

        ["RangeSummaryPlural"] = L(
            "{0} ranges selected · {1} total", "已选择 {0} 个范围 · 共 {1}", "{0} रेंज चुनी गईं · कुल {1}",
            "{0} rangos seleccionados · {1} en total", "{0} plages sélectionnées · {1} au total",
            "تم تحديد {0} نطاقات · الإجمالي {1}", "{0}টি রেঞ্জ নির্বাচিত · মোট {1}",
            "{0} intervalos selecionados · {1} no total", "Выбрано диапазонов: {0} · всего {1}",
            "{0} رینجز منتخب · کل {1}", "Wybrano zakresów: {0} · razem {1}", "Đã chọn {0} khoảng · tổng {1}",
            "เลือก {0} ช่วงเวลา · รวม {1}"),

        ["NoRangesYetText"] = L(
            "No ranges yet - drag the grips or press I / O, then Add range.",
            "还没有范围——拖动手柄或按 I / O，然后点击“添加范围”。",
            "अभी कोई रेंज नहीं - हैंडल खींचें या I / O दबाएं, फिर रेंज जोड़ें पर क्लिक करें।",
            "Aún no hay rangos: arrastra los tiradores o pulsa I / O y luego Añadir rango.",
            "Aucune plage pour l'instant - faites glisser les poignées ou appuyez sur I / O, puis Ajouter une plage.",
            "لا توجد نطاقات بعد - اسحب المقابض أو اضغط I / O، ثم إضافة نطاق.",
            "এখনও কোনো রেঞ্জ নেই - হ্যান্ডেল টানুন বা I / O চাপুন, তারপর রেঞ্জ যোগ করুন চাপুন।",
            "Ainda não há intervalos - arraste as pegas ou prima I / O e depois Adicionar intervalo.",
            "Пока нет диапазонов - перетащите маркеры или нажмите I / O, затем «Добавить диапазон».",
            "ابھی کوئی رینج نہیں - ہینڈلز کھینچیں یا I / O دبائیں، پھر رینج شامل کریں پر کلک کریں۔",
            "Brak zakresów - przeciągnij uchwyty albo naciśnij I / O, a potem Dodaj zakres.",
            "Chưa có khoảng nào - kéo tay cầm hoặc nhấn I / O, sau đó nhấn Thêm khoảng.",
            "ยังไม่มีช่วงเวลา - ลากที่จับหรือกด I / O แล้วกดเพิ่มช่วงเวลา"),

        ["EditingTag"] = L(
            "(editing)", "（编辑中）", "(संपादन में)", "(editando)", "(en cours de modification)", "(قيد التحرير)",
            "(সম্পাদনা করা হচ্ছে)", "(a editar)", "(редактируется)", "(ترمیم میں)", "(edytowany)",
            "(đang chỉnh sửa)", "(กำลังแก้ไข)"),

        ["PlayFromHereTooltip"] = L(
            "Play from here", "从此处播放", "यहाँ से चलाएं", "Reproducir desde aquí", "Lire à partir d'ici",
            "التشغيل من هنا", "এখান থেকে চালান", "Reproduzir a partir daqui", "Воспроизвести отсюда",
            "یہاں سے چلائیں", "Odtwórz od tego miejsca", "Phát từ đây", "เล่นจากตรงนี้"),

        ["EditRangeTooltip"] = L(
            "Edit this range", "编辑此范围", "इस रेंज को संपादित करें", "Editar este rango", "Modifier cette plage",
            "تحرير هذا النطاق", "এই রেঞ্জ সম্পাদনা করুন", "Editar este intervalo", "Изменить этот диапазон",
            "اس رینج میں ترمیم کریں", "Edytuj ten zakres", "Chỉnh sửa khoảng này", "แก้ไขช่วงเวลานี้"),

        ["RemoveRangeTooltip"] = L(
            "Remove this range", "移除此范围", "इस रेंज को हटाएं", "Eliminar este rango", "Supprimer cette plage",
            "إزالة هذا النطاق", "এই রেঞ্জ সরান", "Remover este intervalo", "Удалить этот диапазон",
            "اس رینج کو ہٹائیں", "Usuń ten zakres", "Xóa khoảng này", "ลบช่วงเวลานี้"),

        ["LoadRangeTooltip"] = L(
            "Load this range into Start/End", "将此范围加载到开始/结束", "इस रेंज को स्टार्ट/एंड में लोड करें",
            "Cargar este rango en Inicio/Fin", "Charger cette plage dans Début/Fin",
            "تحميل هذا النطاق إلى البداية/النهاية", "এই রেঞ্জ স্টার্ট/এন্ডে লোড করুন",
            "Carregar este intervalo em Início/Fim", "Загрузить этот диапазон в Начало/Конец",
            "اس رینج کو آغاز/اختتام میں لوڈ کریں", "Wczytaj ten zakres do pól Początek/Koniec",
            "Tải khoảng này vào Bắt đầu/Kết thúc", "โหลดช่วงเวลานี้ลงในเริ่ม/สิ้นสุด"),

        ["NoRangesAddedTitle"] = L(
            "No ranges added", "未添加任何范围", "कोई रेंज नहीं जोड़ी गई", "No se añadió ningún rango",
            "Aucune plage ajoutée", "لم تتم إضافة أي نطاقات", "কোনো রেঞ্জ যোগ করা হয়নি",
            "Nenhum intervalo adicionado", "Диапазоны не добавлены", "کوئی رینج شامل نہیں کی گئی",
            "Nie dodano żadnego zakresu", "Chưa thêm khoảng nào", "ไม่ได้เพิ่มช่วงเวลา"),

        ["NoRangesAddedMessage"] = L(
            "No ranges were added - apply an empty selection?", "未添加任何范围——要应用空选择吗？",
            "कोई रेंज नहीं जोड़ी गई - क्या खाली चयन लागू करें?", "No se añadió ningún rango: ¿aplicar una selección vacía?",
            "Aucune plage n'a été ajoutée - appliquer une sélection vide ?",
            "لم تتم إضافة أي نطاقات - هل تريد تطبيق تحديد فارغ؟", "কোনো রেঞ্জ যোগ করা হয়নি - খালি নির্বাচন প্রয়োগ করবেন?",
            "Não foi adicionado nenhum intervalo - aplicar uma seleção vazia?",
            "Диапазоны не были добавлены - применить пустой выбор?",
            "کوئی رینج شامل نہیں کی گئی - کیا خالی انتخاب لاگو کریں؟",
            "Nie dodano żadnego zakresu - zastosować pusty wybór?", "Chưa thêm khoảng nào - áp dụng lựa chọn trống?",
            "ไม่ได้เพิ่มช่วงเวลา - ต้องการใช้การเลือกที่ว่างเปล่าหรือไม่?"),

        // -- Views/ProjectPreviewPanel.cs -----------------------------------------

        ["ProjectPreviewLabel"] = L(
            "PROJECT PREVIEW", "项目预览", "प्रोजेक्ट पूर्वावलोकन", "VISTA PREVIA DEL PROYECTO", "APERÇU DU PROJET",
            "معاينة المشروع", "প্রকল্প পূর্বরূপ", "PRÉ-VISUALIZAÇÃO DO PROJETO", "ПРЕДПРОСМОТР ПРОЕКТА",
            "پراجیکٹ کا پیش منظر", "PODGLĄD PROJEKTU", "XEM TRƯỚC DỰ ÁN", "ตัวอย่างโปรเจกต์"),

        ["ReloadTooltip"] = L(
            "Reload from the file list", "从文件列表重新加载", "फ़ाइल सूची से फिर से लोड करें",
            "Recargar desde la lista de archivos", "Recharger depuis la liste de fichiers",
            "إعادة التحميل من قائمة الملفات", "ফাইল তালিকা থেকে পুনরায় লোড করুন",
            "Recarregar a partir da lista de ficheiros", "Перезагрузить из списка файлов",
            "فائل فہرست سے دوبارہ لوڈ کریں", "Wczytaj ponownie z listy plików", "Tải lại từ danh sách tệp",
            "โหลดใหม่จากรายการไฟล์"),

        ["ClosePreviewTooltip"] = L(
            "Close the preview", "关闭预览", "पूर्वावलोकन बंद करें", "Cerrar la vista previa", "Fermer l'aperçu",
            "إغلاق المعاينة", "পূর্বরূপ বন্ধ করুন", "Fechar a pré-visualização", "Закрыть предпросмотр",
            "پیش منظر بند کریں", "Zamknij podgląd", "Đóng xem trước", "ปิดตัวอย่าง"),

        ["PreviousSegmentTooltip"] = L(
            "Previous segment", "上一个片段", "पिछला खंड", "Segmento anterior", "Segment précédent",
            "المقطع السابق", "পূর্ববর্তী খণ্ড", "Segmento anterior", "Предыдущий сегмент", "پچھلا حصہ",
            "Poprzedni segment", "Đoạn trước", "ส่วนก่อนหน้า"),

        ["NextSegmentTooltip"] = L(
            "Next segment", "下一个片段", "अगला खंड", "Segmento siguiente", "Segment suivant", "المقطع التالي",
            "পরবর্তী খণ্ড", "Segmento seguinte", "Следующий сегмент", "اگلا حصہ", "Następny segment",
            "Đoạn sau", "ส่วนถัดไป"),

        ["PlayWholeProjectTooltip"] = L(
            "Play the whole project", "播放整个项目", "पूरा प्रोजेक्ट चलाएं", "Reproducir todo el proyecto",
            "Lire tout le projet", "تشغيل المشروع بأكمله", "পুরো প্রকল্প চালান", "Reproduzir o projeto completo",
            "Воспроизвести весь проект", "پورا پراجیکٹ چلائیں", "Odtwórz cały projekt", "Phát toàn bộ dự án",
            "เล่นทั้งโปรเจกต์"),

        ["FileWordSingular"] = L(
            "file", "个文件", "फ़ाइल", "archivo", "fichier", "ملف", "ফাইল", "ficheiro", "файл", "فائل",
            "plik", "tệp", "ไฟล์"),

        ["FileWordPlural"] = L(
            "files", "个文件", "फ़ाइलें", "archivos", "fichiers", "ملفات", "ফাইল", "ficheiros", "файлов",
            "فائلیں", "pliki", "tệp", "ไฟล์"),

        ["SegmentWordSingular"] = L(
            "segment", "个片段", "खंड", "segmento", "segment", "مقطع", "খণ্ড", "segmento", "сегмент", "حصہ",
            "segment", "đoạn", "ส่วน"),

        ["SegmentWordPlural"] = L(
            "segments", "个片段", "खंड", "segmentos", "segments", "مقاطع", "খণ্ড", "segmentos", "сегментов",
            "حصے", "segmenty", "đoạn", "ส่วน"),

        ["NothingToPreviewMessage"] = L(
            "Nothing to preview yet - add files and give each one at least one time range.",
            "还没有可预览的内容——添加文件并为每个文件设置至少一个时间范围。",
            "अभी पूर्वावलोकन के लिए कुछ नहीं - फ़ाइलें जोड़ें और प्रत्येक को कम से कम एक समय रेंज दें।",
            "Aún no hay nada que previsualizar: añade archivos y asigna a cada uno al menos un rango de tiempo.",
            "Rien à prévisualiser pour l'instant - ajoutez des fichiers et donnez à chacun au moins une plage horaire.",
            "لا يوجد شيء لمعاينته بعد - أضف ملفات وأعطِ كل واحد منها نطاقاً زمنياً واحداً على الأقل.",
            "এখনও পূর্বরূপ দেখার কিছু নেই - ফাইল যোগ করুন এবং প্রতিটিকে অন্তত একটি সময়সীমা দিন।",
            "Ainda não há nada para pré-visualizar - adicione ficheiros e dê a cada um pelo menos um intervalo de tempo.",
            "Пока нечего предпросматривать - добавьте файлы и задайте каждому хотя бы один временной диапазон.",
            "ابھی پیش منظر کے لیے کچھ نہیں - فائلیں شامل کریں اور ہر ایک کو کم از کم ایک وقت کی حد دیں۔",
            "Nie ma jeszcze nic do podglądu - dodaj pliki i nadaj każdemu co najmniej jeden zakres czasu.",
            "Chưa có gì để xem trước - hãy thêm tệp và cho mỗi tệp ít nhất một khoảng thời gian.",
            "ยังไม่มีอะไรให้ดูตัวอย่าง - เพิ่มไฟล์และกำหนดช่วงเวลาอย่างน้อยหนึ่งช่วงให้แต่ละไฟล์"),

        ["PreviewPausedDuringJobMessage"] = L(
            "Preview paused while the job runs - the files are being read by the encoder. It comes back on its own when the job finishes.",
            "任务运行期间预览已暂停——编码器正在读取这些文件。任务完成后预览会自动恢复。",
            "जॉब चलने के दौरान पूर्वावलोकन रोका गया - एन्कोडर इन फ़ाइलों को पढ़ रहा है। जॉब पूरा होते ही यह अपने आप लौट आएगा।",
            "Vista previa en pausa mientras se ejecuta la tarea: el codificador está leyendo los archivos. Volverá sola cuando la tarea termine.",
            "Prévisualisation en pause pendant la tâche - l'encodeur est en train de lire les fichiers. Elle revient d'elle-même à la fin de la tâche.",
            "المعاينة متوقفة مؤقتاً أثناء تنفيذ المهمة - يقوم المرمِّز بقراءة الملفات. ستعود تلقائياً عند انتهاء المهمة.",
            "কাজ চলাকালীন পূর্বরূপ থামানো - এনকোডার ফাইলগুলি পড়ছে। কাজ শেষ হলে এটি নিজে থেকেই ফিরে আসবে।",
            "Pré-visualização em pausa enquanto a tarefa corre - o codificador está a ler os ficheiros. Volta sozinha quando a tarefa terminar.",
            "Предпросмотр приостановлен на время задания - файлы читает кодировщик. Он вернётся сам, когда задание закончится.",
            "کام چلنے کے دوران پیش منظر روک دیا گیا ہے - اینکوڈر فائلیں پڑھ رہا ہے۔ کام مکمل ہوتے ہی یہ خود واپس آ جائے گا۔",
            "Podgląd wstrzymany na czas zadania - pliki czyta teraz koder. Wróci sam, gdy zadanie się skończy.",
            "Xem trước tạm dừng trong khi tác vụ chạy - bộ mã hoá đang đọc các tệp. Nó sẽ tự trở lại khi tác vụ xong.",
            "หยุดดูตัวอย่างชั่วคราวขณะที่งานกำลังทำงาน - ตัวเข้ารหัสกำลังอ่านไฟล์อยู่ และจะกลับมาเองเมื่องานเสร็จ"),

        ["ThisFileFallbackName"] = L(
            "this file", "此文件", "यह फ़ाइल", "este archivo", "ce fichier", "هذا الملف", "এই ফাইল",
            "este ficheiro", "этого файла", "اس فائل", "tego pliku", "tệp này", "ไฟล์นี้"),

        ["NoDecoderAnyMessage"] = L(
            "Windows has no decoder for {0}, and nothing else in the project can be previewed. The cut itself is unaffected - ClipGlue uses its own ffmpeg for that.",
            "Windows 没有 {0} 的解码器，项目中的其他内容也无法预览。剪辑本身不受影响——ClipGlue 使用自己的 ffmpeg 完成剪辑。",
            "Windows के पास {0} के लिए डिकोडर नहीं है, और प्रोजेक्ट में कुछ और भी पूर्वावलोकन योग्य नहीं है। कटिंग पर कोई असर नहीं पड़ता - ClipGlue उसके लिए अपना खुद का ffmpeg उपयोग करता है।",
            "Windows no tiene un decodificador para {0}, y no se puede previsualizar nada más del proyecto. El corte en sí no se ve afectado: ClipGlue usa su propio ffmpeg para eso.",
            "Windows n'a pas de décodeur pour {0}, et rien d'autre dans le projet ne peut être prévisualisé. La découpe elle-même n'est pas affectée - ClipGlue utilise son propre ffmpeg pour cela.",
            "لا يملك Windows مفكِّك ترميز لـ {0}، ولا يمكن معاينة أي شيء آخر في المشروع. القص نفسه غير متأثر - يستخدم ClipGlue إصدار ffmpeg الخاص به لذلك.",
            "Windows-এর কাছে {0}-এর ডিকোডার নেই, এবং প্রকল্পের আর কিছুই পূর্বরূপে দেখা যাচ্ছে না। কাটিং নিজে প্রভাবিত হয় না - ClipGlue এর জন্য নিজস্ব ffmpeg ব্যবহার করে।",
            "O Windows não tem um descodificador para {0}, e mais nada no projeto pode ser pré-visualizado. O corte em si não é afetado - o ClipGlue usa o seu próprio ffmpeg para isso.",
            "У Windows нет декодера для {0}, и остальную часть проекта тоже нельзя предпросмотреть. На саму нарезку это не влияет - ClipGlue использует для неё собственный ffmpeg.",
            "Windows کے پاس {0} کے لیے ڈی کوڈر نہیں ہے، اور پراجیکٹ میں کچھ اور بھی پیش منظر کے قابل نہیں۔ کٹنگ خود متاثر نہیں ہوتی - ClipGlue اس کے لیے اپنا ffmpeg استعمال کرتا ہے۔",
            "System Windows nie ma dekodera dla {0}, a nic innego w projekcie też nie da się podejrzeć. Samo cięcie nie jest tym dotknięte - ClipGlue używa do tego własnego ffmpeg.",
            "Windows không có bộ giải mã cho {0}, và không có gì khác trong dự án có thể xem trước được. Bản thân việc cắt không bị ảnh hưởng - ClipGlue dùng ffmpeg riêng của nó cho việc đó.",
            "Windows ไม่มีตัวถอดรหัสสำหรับ {0} และไม่มีอะไรอื่นในโปรเจกต์ที่สามารถดูตัวอย่างได้ การตัดต่อจริงไม่ได้รับผลกระทบ - ClipGlue ใช้ ffmpeg ของตัวเองสำหรับสิ่งนั้น"),

        ["SkippingFileMessage"] = L(
            "Skipping {0} - Windows has no decoder for it. The cut itself is unaffected.",
            "跳过 {0}——Windows 没有其解码器。剪辑本身不受影响。",
            "{0} को छोड़ा जा रहा है - Windows के पास इसके लिए डिकोडर नहीं है। कटिंग पर कोई असर नहीं पड़ता।",
            "Omitiendo {0}: Windows no tiene un decodificador para él. El corte en sí no se ve afectado.",
            "{0} ignoré - Windows n'a pas de décodeur pour ce fichier. La découpe elle-même n'est pas affectée.",
            "تخطي {0} - لا يملك Windows مفكِّك ترميز له. القص نفسه غير متأثر.",
            "{0} বাদ দেওয়া হচ্ছে - Windows-এর কাছে এর ডিকোডার নেই। কাটিং নিজে প্রভাবিত হয় না।",
            "A ignorar {0} - o Windows não tem um descodificador para ele. O corte em si não é afetado.",
            "Пропуск {0} - у Windows нет декодера для него. На саму нарезку это не влияет.",
            "{0} کو نظر انداز کیا جا رہا ہے - Windows کے پاس اس کا ڈی کوڈر نہیں ہے۔ کٹنگ خود متاثر نہیں ہوتی۔",
            "Pomijanie {0} - system Windows nie ma dla niego dekodera. Samo cięcie nie jest tym dotknięte.",
            "Đang bỏ qua {0} - Windows không có bộ giải mã cho nó. Bản thân việc cắt không bị ảnh hưởng.",
            "กำลังข้าม {0} - Windows ไม่มีตัวถอดรหัสสำหรับไฟล์นี้ การตัดต่อจริงไม่ได้รับผลกระทบ"),

        ["SettingsButtonTooltip"] = L(
            "Settings", "设置", "सेटिंग्स", "Configuración", "Paramètres", "الإعدادات", "সেটিংস",
            "Definições", "Настройки", "ترتیبات", "Ustawienia", "Cài đặt", "การตั้งค่า"),

        ["CopyLogTooltip"] = L(
            "Copy console log", "复制控制台日志", "कंसोल लॉग कॉपी करें", "Copiar registro de la consola",
            "Copier le journal de la console", "نسخ سجل وحدة التحكم", "কনসোল লগ কপি করুন",
            "Copiar registo da consola", "Скопировать журнал консоли", "کنسول لاگ کاپی کریں",
            "Kopiuj log konsoli", "Sao chép nhật ký bảng điều khiển", "คัดลอกล็อกคอนโซล"),

        ["SettingsPanelTitle"] = L(
            "Settings", "设置", "सेटिंग्स", "Configuración", "Paramètres", "الإعدادات", "সেটিংস",
            "Definições", "Настройки", "ترتیبات", "Ustawienia", "Cài đặt", "การตั้งค่า"),

        ["RemoveFileAssociationAction"] = L(
            "Remove from Windows registry", "从 Windows 注册表中移除", "Windows रजिस्ट्री से हटाएं",
            "Quitar del registro de Windows", "Supprimer du registre Windows", "إزالة من سجل Windows",
            "উইন্ডোজ রেজিস্ট্রি থেকে সরান", "Remover do registo do Windows", "Удалить из реестра Windows",
            "ونڈوز رجسٹری سے ہٹائیں", "Usuń z rejestru Windows", "Xóa khỏi registry Windows",
            "ลบออกจากรีจิสทรีของ Windows"),

        ["RestoreFileAssociationAction"] = L(
            "Register with Windows again", "重新在 Windows 中注册", "Windows में फिर से पंजीकृत करें",
            "Registrar de nuevo en Windows", "Réenregistrer dans Windows", "إعادة التسجيل في Windows",
            "Windows-এ আবার নিবন্ধন করুন", "Registar novamente no Windows", "Зарегистрировать в Windows снова",
            "ونڈوز میں دوبارہ رجسٹر کریں", "Zarejestruj ponownie w Windows", "Đăng ký lại với Windows",
            "ลงทะเบียนกับ Windows อีกครั้ง"),

        ["FileAssociationDescription"] = L(
            "Controls whether double-clicking a .clipglue file opens it here.",
            "控制双击 .clipglue 文件时是否在此处打开。",
            "नियंत्रित करता है कि .clipglue फ़ाइल पर डबल-क्लिक करने से वह यहां खुलेगी या नहीं।",
            "Controla si al hacer doble clic en un archivo .clipglue se abre aquí.",
            "Détermine si un double clic sur un fichier .clipglue l'ouvre ici.",
            "يتحكم فيما إذا كان النقر المزدوج على ملف .clipglue يفتحه هنا.",
            "একটি .clipglue ফাইলে ডাবল-ক্লিক করলে তা এখানে খুলবে কিনা তা নিয়ন্ত্রণ করে।",
            "Controla se clicar duas vezes num ficheiro .clipglue o abre aqui.",
            "Определяет, открывается ли файл .clipglue здесь при двойном щелчке.",
            "کنٹرول کرتا ہے کہ .clipglue فائل پر ڈبل کلک کرنے سے وہ یہاں کھلے گی یا نہیں۔",
            "Decyduje, czy dwuklik na pliku .clipglue otwiera go w tym programie.",
            "Kiểm soát việc nhấp đúp vào tệp .clipglue có mở tệp đó ở đây hay không.",
            "ควบคุมว่าการดับเบิลคลิกไฟล์ .clipglue จะเปิดไฟล์นั้นที่นี่หรือไม่"),

        ["SegmentStripToggleAction"] = L(
            "Show the kept/cut strip", "显示保留/剪切条", "रखे/काटे गए स्ट्रिप दिखाएँ", "Mostrar la franja de conservado/cortado",
            "Afficher la barre conservé/coupé", "إظهار شريط المحتفظ به/المقصوص", "রাখা/কাটা স্ট্রিপ দেখান",
            "Mostrar a faixa de mantido/cortado", "Показывать полосу «сохранено/вырезано»", "رکھی/کاٹی گئی پٹی دکھائیں",
            "Pokaż pasek zachowane/wycięte", "Hiện dải giữ lại/đã cắt", "แสดงแถบเก็บไว้/ตัดออก"),

        ["SegmentStripToggleDescription"] = L(
            "The bar under the seek bar that maps each file's kept and cut ranges. Turning it off gives the video more room.",
            "在进度条下方显示每个文件保留和剪切范围的条形图。关闭后视频画面可获得更多空间。",
            "सीक बार के नीचे वह पट्टी जो हर फ़ाइल के रखे और काटे गए हिस्से दिखाती है। इसे बंद करने से वीडियो को अधिक जगह मिलती है।",
            "La franja bajo la barra de búsqueda que muestra los intervalos conservados y cortados de cada archivo. Al desactivarla, el vídeo gana más espacio.",
            "La barre sous la barre de recherche qui indique les plages conservées et coupées de chaque fichier. La désactiver laisse plus de place à la vidéo.",
            "الشريط الموجود أسفل شريط البحث الذي يعرض النطاقات المحتفظ بها والمقصوصة من كل ملف. إيقافه يمنح الفيديو مساحة أكبر.",
            "সিক বারের নিচের এই স্ট্রিপটি প্রতিটি ফাইলের রাখা ও কাটা অংশ দেখায়। বন্ধ করলে ভিডিওর জন্য বেশি জায়গা মেলে।",
            "A faixa sob a barra de procura que mostra os intervalos mantidos e cortados de cada ficheiro. Desativá-la dá mais espaço ao vídeo.",
            "Полоса под шкалой перемотки, показывающая сохранённые и вырезанные диапазоны каждого файла. Отключение освобождает больше места под видео.",
            "سیک بار کے نیچے وہ پٹی جو ہر فائل کے رکھے اور کاٹے گئے حصے دکھاتی ہے۔ اسے بند کرنے سے ویڈیو کو زیادہ جگہ ملتی ہے۔",
            "Pasek pod suwakiem odtwarzania pokazujący zachowane i wycięte zakresy każdego pliku. Wyłączenie go daje więcej miejsca dla obrazu.",
            "Thanh dưới thanh tua hiển thị các khoảng đã giữ và đã cắt của từng tệp. Tắt đi sẽ nhường thêm chỗ cho video.",
            "แถบใต้แถบเลื่อนตำแหน่งที่แสดงช่วงที่เก็บไว้และตัดออกของแต่ละไฟล์ ปิดไว้จะทำให้วิดีโอมีพื้นที่มากขึ้น"),

        ["VerboseLoggingAction"] = L(
            "Verbose ffmpeg logging", "详细的 ffmpeg 日志", "विस्तृत ffmpeg लॉगिंग", "Registro detallado de ffmpeg",
            "Journalisation détaillée de ffmpeg", "تسجيل مفصل لـ ffmpeg", "বিস্তারিত ffmpeg লগিং",
            "Registo detalhado do ffmpeg", "Подробное логирование ffmpeg", "تفصیلی ffmpeg لاگنگ",
            "Szczegółowe logowanie ffmpeg", "Ghi nhật ký ffmpeg chi tiết", "การบันทึกล็อก ffmpeg แบบละเอียด"),

        ["VerboseLoggingDescription"] = L(
            "Logs every ffmpeg command and its full output to the console below - useful when reporting a problem.",
            "将每条 ffmpeg 命令及其完整输出记录到下方的控制台——报告问题时很有用。",
            "हर ffmpeg कमांड और उसका पूरा आउटपुट नीचे के कंसोल में लॉग करता है - समस्या की रिपोर्ट करते समय उपयोगी।",
            "Registra cada comando de ffmpeg y su salida completa en la consola de abajo - útil al reportar un problema.",
            "Enregistre chaque commande ffmpeg et sa sortie complète dans la console ci-dessous - utile pour signaler un problème.",
            "يسجل كل أمر ffmpeg وناتجه الكامل في وحدة التحكم أدناه - مفيد عند الإبلاغ عن مشكلة.",
            "প্রতিটি ffmpeg কমান্ড ও তার সম্পূর্ণ আউটপুট নিচের কনসোলে লগ করে - সমস্যা রিপোর্ট করার সময় কাজে আসে।",
            "Regista cada comando ffmpeg e a sua saída completa na consola abaixo - útil ao comunicar um problema.",
            "Записывает каждую команду ffmpeg и её полный вывод в консоль ниже - полезно при сообщении о проблеме.",
            "ہر ffmpeg کمانڈ اور اس کا مکمل آؤٹ پٹ نیچے کنسول میں لاگ کرتا ہے - مسئلے کی اطلاع دیتے وقت مفید۔",
            "Zapisuje każde polecenie ffmpeg i jego pełne wyjście w konsoli poniżej - przydatne przy zgłaszaniu problemu.",
            "Ghi lại mọi lệnh ffmpeg và toàn bộ kết quả của nó vào bảng điều khiển bên dưới - hữu ích khi báo cáo sự cố.",
            "บันทึกคำสั่ง ffmpeg ทุกคำสั่งและผลลัพธ์ทั้งหมดลงในคอนโซลด้านล่าง - มีประโยชน์เมื่อรายงานปัญหา"),

        ["DetailedProgressToggleAction"] = L(
            "Detailed progress view", "详细进度视图", "विस्तृत प्रगति दृश्य", "Vista de progreso detallada",
            "Vue de progression détaillée", "عرض التقدم المفصل", "বিস্তারিত অগ্রগতি দৃশ্য",
            "Vista de progresso detalhada", "Подробный вид прогресса", "تفصیلی پیش رفت منظر",
            "Szczegółowy widok postępu", "Xem tiến trình chi tiết", "มุมมองความคืบหน้าแบบละเอียด"),

        ["DetailedProgressToggleDescription"] = L(
            "Shows both progress bars, the current file name and CPU/memory stats. Off by default, which shows a compact bar at the bottom instead.",
            "显示两个进度条、当前文件名以及 CPU/内存统计信息。默认关闭，此时改为在底部显示一个精简的进度条。",
            "दोनों प्रगति बार, वर्तमान फ़ाइल का नाम और CPU/मेमोरी आँकड़े दिखाता है। डिफ़ॉल्ट रूप से बंद है, जिससे इसके बजाय नीचे एक संक्षिप्त बार दिखाई देता है।",
            "Muestra ambas barras de progreso, el nombre del archivo actual y las estadísticas de CPU/memoria. Desactivada por defecto, lo que muestra en su lugar una barra compacta en la parte inferior.",
            "Affiche les deux barres de progression, le nom du fichier en cours et les statistiques CPU/mémoire. Désactivée par défaut, ce qui affiche à la place une barre compacte en bas.",
            "يعرض كلا شريطي التقدم واسم الملف الحالي وإحصاءات المعالج/الذاكرة. معطل افتراضيًا، ويظهر بدلاً منه شريط مضغوط في الأسفل.",
            "উভয় অগ্রগতি বার, বর্তমান ফাইলের নাম এবং CPU/মেমরি পরিসংখ্যান দেখায়। ডিফল্টরূপে বন্ধ থাকে, যার ফলে পরিবর্তে নিচে একটি সংক্ষিপ্ত বার দেখানো হয়।",
            "Mostra ambas as barras de progresso, o nome do ficheiro atual e as estatísticas de CPU/memória. Desativada por predefinição, o que mostra em vez disso uma barra compacta em baixo.",
            "Показывает обе полосы прогресса, имя текущего файла и статистику ЦП/памяти. По умолчанию отключено — вместо этого внизу показывается компактная полоса.",
            "دونوں پیش رفت بارز، موجودہ فائل کا نام اور CPU/میموری اعدادوشمار دکھاتا ہے۔ پہلے سے غیر فعال ہے، جس کی وجہ سے اس کے بجائے نیچے ایک مختصر بار دکھائی دیتی ہے۔",
            "Pokazuje oba paski postępu, nazwę aktualnie przetwarzanego pliku oraz statystyki CPU/pamięci. Domyślnie wyłączony — wtedy na dole pokazywany jest kompaktowy pasek.",
            "Hiển thị cả hai thanh tiến trình, tên tệp hiện tại và số liệu CPU/bộ nhớ. Mặc định tắt, khi đó một thanh gọn sẽ hiển thị ở dưới cùng thay thế.",
            "แสดงแถบความคืบหน้าทั้งสอง ชื่อไฟล์ปัจจุบัน และสถิติ CPU/หน่วยความจำ ปิดโดยค่าเริ่มต้น ซึ่งจะแสดงแถบขนาดกะทัดรัดที่ด้านล่างแทน"),

        ["StatSpeedLabel"] = L(
            "Speed", "速度", "गति", "Velocidad", "Vitesse", "السرعة", "গতি", "Velocidade",
            "Скорость", "رفتار", "Prędkość", "Tốc độ", "ความเร็ว"),

        ["StatEtaLabel"] = L(
            "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA", "ETA"),

        ["StatCpuLabel"] = L(
            "CPU (avg)", "CPU（平均）", "CPU (औसत)", "CPU (prom.)", "CPU (moy.)", "المعالج (متوسط)",
            "CPU (গড়)", "CPU (méd.)", "ЦП (сред.)", "CPU (اوسط)", "CPU (śr.)", "CPU (TB)", "CPU (เฉลี่ย)"),

        ["StatMemoryLabel"] = L(
            "Memory (avg)", "内存（平均）", "मेमोरी (औसत)", "Memoria (prom.)", "Mémoire (moy.)",
            "الذاكرة (متوسط)", "মেমরি (গড়)", "Memória (méd.)", "Память (сред.)", "میموری (اوسط)",
            "Pamięć (śr.)", "Bộ nhớ (TB)", "หน่วยความจำ (เฉลี่ย)"),

        // -- redesign: collapsed instruction bar, chips, console drawer -------

        // The one line left showing when the "How it works" panel is
        // collapsed. Deliberately not a truncation of HeaderLine1: a summary
        // has to say what to do, not trail off mid-sentence.
        // Split in two around the mono example chip that sits between them in
        // the collapsed bar, so the chip is part of the sentence rather than
        // a separate line.
        ["HeaderSummaryLead"] = L(
            "enter the ranges to keep, e.g.",
            "输入要保留的时间段，例如",
            "रखने के लिए समय-सीमाएँ दर्ज करें, जैसे",
            "introduce los intervalos a conservar, p. ej.",
            "saisissez les plages à conserver, p. ex.",
            "أدخل النطاقات المراد الاحتفاظ بها، مثل",
            "রাখতে চাওয়া সময়সীমা লিখুন, যেমন",
            "introduza os intervalos a manter, p. ex.",
            "введите диапазоны для сохранения, например",
            "رکھنے کے لیے وقفے درج کریں، مثلاً",
            "podaj zakresy do zachowania, np.",
            "nhập các khoảng cần giữ, ví dụ",
            "ป้อนช่วงเวลาที่ต้องการเก็บไว้ เช่น"),

        ["HeaderSummaryTail"] = L(
            "— or click ▷ on a file to pick them visually.",
            "— 或点击文件上的 ▷ 直观选择。",
            "— या दृश्य रूप से चुनने के लिए फ़ाइल पर ▷ क्लिक करें।",
            "— o haz clic en ▷ junto a un archivo para elegirlos visualmente.",
            "— ou cliquez sur ▷ à côté d’un fichier pour les choisir visuellement.",
            "— أو انقر على ▷ بجوار الملف لاختيارها بصريًا.",
            "— বা দৃশ্যত বেছে নিতে ফাইলের পাশে ▷ ক্লিক করুন।",
            "— ou clique em ▷ num ficheiro para os escolher visualmente.",
            "— или нажмите ▷ у файла, чтобы выбрать их визуально.",
            "— یا بصری طور پر منتخب کرنے کے لیے فائل کے پاس ▷ پر کلک کریں۔",
            "— albo kliknij ▷ przy pliku, by wybrać je wizualnie.",
            "— hoặc nhấp ▷ ở tệp để chọn trực quan.",
            "— หรือคลิก ▷ ที่ไฟล์เพื่อเลือกแบบเห็นภาพ"),

        ["ExpandLabel"] = L(
            "Expand", "展开", "विस्तार करें", "Ampliar", "Déplier", "توسيع", "প্রসারিত করুন", "Expandir",
            "Развернуть", "پھیلائیں", "Rozwiń", "Mở rộng", "ขยาย"),

        ["CollapseLabel"] = L(
            "Collapse", "收起", "छोटा करें", "Contraer", "Replier", "طي", "সংকুচিত করুন", "Recolher",
            "Свернуть", "سکیڑیں", "Zwiń", "Thu gọn", "ย่อ"),

        // Label of the raw comma-separated text field kept underneath the
        // chips for people who would rather type the whole thing at once.
        ["ManualEntryLabel"] = L(
            "Type manually", "手动输入", "मैन्युअल रूप से लिखें", "Escribir a mano", "Saisir à la main",
            "إدخال يدوي", "নিজে লিখুন", "Escrever manualmente", "Ввести вручную", "دستی طور پر لکھیں",
            "Wpisz ręcznie", "Nhập thủ công", "พิมพ์เอง"),

        ["ManualEntryTooltip"] = L(
            "Type every range as text, separated by commas",
            "以文本形式输入所有时间段，用逗号分隔",
            "सभी समय-सीमाएँ पाठ के रूप में लिखें, अल्पविराम से अलग करके",
            "Escribe todos los intervalos como texto, separados por comas",
            "Saisissez toutes les plages sous forme de texte, séparées par des virgules",
            "اكتب كل النطاقات كنص، مفصولة بفواصل",
            "সব সময়সীমা পাঠ্য হিসেবে লিখুন, কমা দিয়ে আলাদা করে",
            "Escreva todos os intervalos como texto, separados por vírgulas",
            "Введите все диапазоны текстом через запятую",
            "تمام وقفے متن کے طور پر لکھیں، کوما سے الگ کر کے",
            "Wpisz wszystkie zakresy jako tekst, oddzielone przecinkami",
            "Nhập tất cả các khoảng dưới dạng văn bản, phân tách bằng dấu phẩy",
            "พิมพ์ช่วงเวลาทั้งหมดเป็นข้อความ คั่นด้วยจุลภาค"),

        // Count of log lines written since the console drawer was last open.
        // "{0}" is the number.
        ["ConsoleNewLinesLabel"] = L(
            "{0} new", "{0} 条新", "{0} नए", "{0} nuevas", "{0} nouvelles", "{0} جديد", "{0}টি নতুন",
            "{0} novas", "{0} новых", "{0} نئی", "{0} nowych", "{0} mới", "ใหม่ {0}"),

        ["SegmentsKeptLabel"] = L(
            "kept", "保留", "रखा गया", "conservado", "conservé", "محتفظ به", "রাখা", "mantido",
            "сохранено", "رکھا گیا", "zachowane", "giữ lại", "เก็บไว้"),

        // Small uppercase caption over the progress card, with the file
        // currently being cut shown at the other end of the same line.
        ["FileProgressHeader"] = L(
            "FILE PROGRESS", "文件进度", "फ़ाइल प्रगति", "PROGRESO DEL ARCHIVO", "PROGRESSION DU FICHIER",
            "تقدم الملف", "ফাইলের অগ্রগতি", "PROGRESSO DO FICHEIRO", "ПРОГРЕСС ФАЙЛА", "فائل کی پیش رفت",
            "POSTĘP PLIKU", "TIẾN ĐỘ TỆP", "ความคืบหน้าของไฟล์"),

        // Suffix on the overall bar's percentage: "34% of total".
        ["OfTotalLabel"] = L(
            "of total", "总进度", "कुल का", "del total", "du total", "من الإجمالي", "মোটের",
            "do total", "от всего", "کل کا", "całości", "tổng cộng", "ของทั้งหมด"),

        ["SegmentsCutLabel"] = L(
            "cut", "剪掉", "हटाया गया", "cortado", "coupé", "مقصوص", "কাটা", "cortado",
            "вырезано", "کاٹا گیا", "wycięte", "cắt bỏ", "ตัดออก"),
    };
}
