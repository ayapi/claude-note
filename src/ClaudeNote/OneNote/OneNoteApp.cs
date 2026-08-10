using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace ClaudeNote;

/// <summary>
/// デスクトップ版 OneNote の COM API ラッパー。
/// OneNote の IDispatch は型情報の取得に失敗するため、dynamic や InvokeMember の
/// 遅延バインディングは使えない。GAC の Interop アセンブリ (PIA) をロードし、
/// 管理ラッパークラス Application2Class をリフレクションで呼び出す。
///
/// 重要: 使い終わったら必ず Dispose すること。COM 参照を掴んだままにすると、
/// ユーザーが OneNote を閉じてもプロセスが終了できず、次に起動したときに
/// 「前回開いた OneNote のクリーンアップ作業中です」と出て起動できなくなる。
/// </summary>
public sealed class OneNoteApp : IDisposable
{
    private const string PiaNamespace = "Microsoft.Office.Interop.OneNote.";

    private readonly Assembly _pia;
    private readonly Type _type;
    private readonly object _app;

    public OneNoteApp()
    {
        _pia = LoadPia();
        var type = _pia.GetType(PiaNamespace + "Application2Class")
            ?? _pia.GetType(PiaNamespace + "ApplicationClass")
            ?? throw new UserFacingException("OneNote の Interop アセンブリに ApplicationClass が見つかりません。");
        try
        {
            _app = Activator.CreateInstance(type)
                ?? throw new UserFacingException("OneNote に接続できませんでした。");
        }
        catch (TargetInvocationException ex)
        {
            Logger.Log(ex);
            throw new UserFacingException("OneNote に接続できませんでした。デスクトップ版 OneNote が起動できる状態か確認してください。");
        }
        _type = _app.GetType();
    }

    private static Assembly LoadPia()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "assembly", "GAC_MSIL", "Microsoft.Office.Interop.OneNote");
        if (Directory.Exists(root))
        {
            // GAC には旧バージョン (v12 など) も残っていることがあるため、新しい順に試す
            var candidates = Directory.GetDirectories(root)
                .Select(dir => Path.Combine(dir, "Microsoft.Office.Interop.OneNote.dll"))
                .Where(File.Exists)
                .Select(dll =>
                {
                    try { return (Dll: dll, Version: AssemblyName.GetAssemblyName(dll).Version ?? new Version(0, 0)); }
                    catch { return (Dll: dll, Version: new Version(0, 0)); }
                })
                .OrderByDescending(c => c.Version);

            foreach (var (dll, _) in candidates)
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    // XMLSchema enum が無い世代 (2007 PIA) は使えない
                    if (asm.GetType(PiaNamespace + "XMLSchema") != null) return asm;
                    Logger.Log($"古い PIA をスキップ: {dll}");
                }
                catch (Exception ex) { Logger.Log($"PIA のロードに失敗 ({dll}): {ex.Message}"); }
            }
        }
        throw new UserFacingException("OneNote の Interop アセンブリが見つかりません。デスクトップ版 OneNote がインストールされているか確認してください。");
    }

    /// <summary>現在アクティブなページの ID。ページが開かれていなければ空文字。</summary>
    public string GetCurrentPageId() => GetCurrentContext().PageId;

    /// <summary>現在アクティブなページとセクションの ID。取得できなければ空文字。</summary>
    public (string PageId, string SectionId) GetCurrentContext()
    {
        object? windows = null;
        object? current = null;
        try
        {
            // Application2Class.Windows は生の __ComObject を返すため、実行時型ではなく
            // PIA の ComImport インターフェイス型 (QI 経由) でプロパティを引く必要がある
            windows = GetProp(_app, "Windows");
            if (windows == null) return ("", "");
            var winsIface = _pia.GetType(PiaNamespace + "Windows", true)!;
            var winIface = _pia.GetType(PiaNamespace + "Window", true)!;
            current = GetPropVia(winsIface, windows, "CurrentWindow");
            if (current == null) return ("", "");
            var pageId = GetPropVia(winIface, current, "CurrentPageId") as string ?? "";
            var sectionId = GetPropVia(winIface, current, "CurrentSectionId") as string ?? "";
            return (pageId, sectionId);
        }
        catch (Exception ex)
        {
            Logger.Log($"現在ページの取得に失敗: {ex}");
            return ("", "");
        }
        finally
        {
            // 中間オブジェクトも COM 参照なので必ず解放する
            Release(current);
            Release(windows);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject == null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch (Exception ex)
        {
            Logger.Log($"COM オブジェクトの解放に失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// OneNote への参照を手放す。これを怠ると OneNote のプロセスが終了できなくなる。
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Marshal.IsComObject(_app)) Marshal.FinalReleaseComObject(_app);
        }
        catch (Exception ex)
        {
            Logger.Log($"OneNote への参照の解放に失敗: {ex.Message}");
        }
    }

    /// <summary>選択マーカーとバイナリ (ink ISF / 画像) 込みのページ XML を取得。</summary>
    public string GetPageXml(string pageId)
    {
        // PageInfo.piBinaryDataSelection = 3
        var pageInfo = EnumType("PageInfo");
        var schema = EnumType("XMLSchema");
        var args = new object?[] { pageId, null, Enum.ToObject(pageInfo, 3), Xs2013 };
        Invoke("GetPageContent", [typeof(string), typeof(string).MakeByRefType(), pageInfo, schema], args);
        return args[1] as string
            ?? throw new UserFacingException("ページ内容を取得できませんでした。");
    }

    /// <summary>
    /// バイナリ (ink の ISF や画像) を含まない軽いページ XML。
    /// 位置とサイズは入っているので、挿入位置の計算だけならこちらで足りる。
    /// </summary>
    public string GetPageXmlBasic(string pageId) => GetPageXmlWith(pageId, 0);

    /// <summary>
    /// 選択マーカー付き・バイナリ無しのページ XML。
    /// 「何が選ばれているか」を調べるだけならこれで足り、巨大なページでも速い。
    /// </summary>
    public string GetPageXmlSelectionOnly(string pageId) => GetPageXmlWith(pageId, 2);

    private string GetPageXmlWith(string pageId, int pageInfoValue)
    {
        var pageInfo = EnumType("PageInfo");
        var schema = EnumType("XMLSchema");
        var args = new object?[] { pageId, null, Enum.ToObject(pageInfo, pageInfoValue), Xs2013 };
        Invoke("GetPageContent", [typeof(string), typeof(string).MakeByRefType(), pageInfo, schema], args);
        return args[1] as string
            ?? throw new UserFacingException("ページ内容を取得できませんでした。");
    }

    public void UpdatePage(string pageChangesXml)
    {
        var schema = EnumType("XMLSchema");
        try
        {
            // dateExpectedLastModified = MinValue は「更新競合チェックなし」の意味
            Invoke("UpdatePageContent",
                [typeof(string), typeof(DateTime), schema, typeof(bool)],
                [pageChangesXml, DateTime.MinValue, Xs2013, false]);
        }
        catch (ArgumentException)
        {
            // DateTime.MinValue が OLE DATE に変換できない環境向けフォールバック
            Invoke("UpdatePageContent", [typeof(string)], [pageChangesXml]);
        }
    }

    public string GetHierarchyXml()
    {
        // HierarchyScope.hsPages = 4
        var scope = EnumType("HierarchyScope");
        var schema = EnumType("XMLSchema");
        var args = new object?[] { "", Enum.ToObject(scope, 4), null, Xs2013 };
        Invoke("GetHierarchy", [typeof(string), scope, typeof(string).MakeByRefType(), schema], args);
        return args[2] as string ?? "";
    }

    /// <summary>セクション ID からセクション名を引く。見つからなければ空文字。</summary>
    public string GetSectionName(string sectionId)
    {
        try
        {
            // HierarchyScope.hsSections = 3 (ページまで取る hsPages より軽い)
            var scope = EnumType("HierarchyScope");
            var schema = EnumType("XMLSchema");
            var args = new object?[] { "", Enum.ToObject(scope, 3), null, Xs2013 };
            Invoke("GetHierarchy", [typeof(string), scope, typeof(string).MakeByRefType(), schema], args);
            if (args[2] is not string xml) return "";
            return XDocument.Parse(xml)
                .Descendants(PageXml.One + "Section")
                .FirstOrDefault(s => (string?)s.Attribute("ID") == sectionId)
                ?.Attribute("name")?.Value ?? "";
        }
        catch (Exception ex)
        {
            Logger.Log($"セクション名の取得に失敗: {ex.Message}");
            return "";
        }
    }

    /// <summary>XMLSchema.xs2013 (= 2)。</summary>
    private object Xs2013 => Enum.ToObject(EnumType("XMLSchema"), 2);

    public string CreateNewPage(string sectionId)
    {
        var args = new object?[] { sectionId, null };
        Invoke("CreateNewPage", [typeof(string), typeof(string).MakeByRefType()], args);
        return args[1] as string
            ?? throw new UserFacingException("ページを作成できませんでした。");
    }

    public void DeleteHierarchyItem(string objectId)
    {
        Invoke("DeleteHierarchy", [typeof(string)], [objectId]);
    }

    private Type EnumType(string name) => _pia.GetType(PiaNamespace + name, throwOnError: true)!;

    private static object? GetProp(object target, string name) =>
        GetPropVia(target.GetType(), target, name);

    /// <summary>COM の RCW (__ComObject) に対して、指定したインターフェイス型経由でプロパティを読む。</summary>
    private static object? GetPropVia(Type type, object target, string name)
    {
        var prop = type.GetProperty(name)
            ?? throw new InvalidOperationException($"{type.Name}.{name} が見つかりません。");
        try
        {
            return prop.GetValue(target);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private void Invoke(string method, Type[] signature, object?[] args)
    {
        var mi = _type.GetMethod(method, signature)
            ?? throw new InvalidOperationException($"{_type.Name}.{method} が見つかりません。");
        try
        {
            mi.Invoke(_app, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
