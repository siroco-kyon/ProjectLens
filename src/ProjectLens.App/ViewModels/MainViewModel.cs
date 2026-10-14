using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using ProjectLens.App.Services;
using ProjectLens.Core.Analysis;
using ProjectLens.Core.Context;
using ProjectLens.Core.Export;
using ProjectLens.Core.Models;

namespace ProjectLens.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IProjectAnalyzer _analyzer;
    private readonly IUserInteraction _interaction;
    private readonly ContextGenerator _generator = new();
    private readonly ContextExporter _exporter = new();
    private CancellationTokenSource? _operation;
    private AnalysisResult? _result;
    private bool _analysisDirty = true;
    private ContextDocument? _document;
    private bool _busy;
    private bool _indeterminate;
    private double _progress;
    private string _inputPath = "";
    private string _excludedDirectories = "bin;obj;.git;.vs;node_modules;packages";
    private string _includedRelativePaths = "";
    private string _fileFilter = "";
    private string _resultsFilter = "";
    private string _status = "解析するフォルダー、ソリューション、またはプロジェクトを選択してください。";
    private string _error = "";
    private string _request = "";
    private string _partSizeText = "12000";
    private string _outputDirectory = "";
    private bool _includeSource;
    private Choice<ContextKind> _selectedKind;
    private Choice<RelatedFileDirection> _selectedRelatedDirection;
    private Choice<int> _selectedRelatedDepth;
    private Choice<PromptPurpose> _selectedPurpose;
    private Choice<string> _selectedFormat;
    private ContextPart? _selectedPart;
    private FileItem? _selectedFile;
    private string _previewHint = "解析結果から資料を生成すると、ここに表示されます。";
    private string _exportedFiles = "";

    public MainViewModel(IProjectAnalyzer analyzer, IUserInteraction interaction)
    {
        _analyzer = analyzer;
        _interaction = interaction;
        _selectedKind = ContextKinds[0];
        _selectedRelatedDirection = RelatedDirections[3];
        _selectedRelatedDepth = RelatedDepths[0];
        _selectedPurpose = PromptPurposes[0];
        _selectedFormat = ExportFormats[0];
        TypesView = CollectionViewSource.GetDefaultView(TypeItems);
        MembersView = CollectionViewSource.GetDefaultView(MemberItems);
        FilesView = CollectionViewSource.GetDefaultView(FileItems);
        ProjectsView = CollectionViewSource.GetDefaultView(ProjectItems);
        RelationshipsView = CollectionViewSource.GetDefaultView(RelationshipItems);
        DiagnosticsView = CollectionViewSource.GetDefaultView(Diagnostics);
        TypesView.Filter = item => item is TypeItem t && Matches(ResultsFilter, t.FullName, t.Kind, t.FilePath, t.Bases);
        MembersView.Filter = item => item is MemberItem m && Matches(ResultsFilter, m.TypeName, m.Name, m.FilePath, m.Signature);
        FilesView.Filter = item => item is FileItem f && Matches(ResultsFilter, f.RelativePath, f.ProjectName, f.RoleNote, f.Kind);
        ProjectsView.Filter = item => item is ProjectItem p && Matches(ResultsFilter, p.Name, p.Framework, p.RelativePath, p.References, p.Packages);
        RelationshipsView.Filter = item => item is RelationshipItem r && Matches(ResultsFilter, r.FromFile, r.ToFile, r.Kind, r.Evidence, r.Confidence);
        DiagnosticsView.Filter = item => item is AnalysisDiagnostic d && Matches(ResultsFilter, d.FilePath, d.Severity, d.Message);

        BrowseFolderCommand = new(() => RunAction(() => { var path = _interaction.ChooseInputFolder(); if (path is not null) InputPath = path; }), () => CanEdit);
        BrowseFileCommand = new(() => RunAction(() => { var path = _interaction.ChooseInputFile(); if (path is not null) InputPath = path; }), () => CanEdit);
        AnalyzeCommand = new(AnalyzeAsync, () => CanEdit && !string.IsNullOrWhiteSpace(InputPath));
        CancelCommand = new(Cancel, () => IsBusy && _operation is { IsCancellationRequested: false });
        GenerateCommand = new(GenerateAsync, () => CanEdit && _result is not null && !_analysisDirty);
        CopyCommand = new(() => RunAction(CopySelectedPart), () => CanEdit && _document is not null && SelectedPart is not null);
        BrowseOutputCommand = new(() => RunAction(() => { var path = _interaction.ChooseOutputFolder(); if (path is not null) OutputDirectory = path; }), () => CanEdit);
        ExportCommand = new(ExportAsync, () => CanEdit && _document is not null && !string.IsNullOrWhiteSpace(OutputDirectory));
        SelectVisibleFilesCommand = new(() => { foreach (var file in FileItems.Where(FileMatches)) file.InContext = true; }, () => CanEdit && _result is not null);
        ClearSelectionCommand = new(() => { foreach (var file in FileItems) file.InContext = false; foreach (var type in TypeItems) type.InContext = false; }, () => CanEdit && _result is not null);
    }

    public IReadOnlyList<Choice<ContextKind>> ContextKinds { get; } =
    [new("A. プロジェクト全体概要", ContextKind.Overview), new("B. コード構造一覧", ContextKind.Structure), new("C. 選択ファイル・型の解析", ContextKind.Feature)];
    public IReadOnlyList<Choice<PromptPurpose>> PromptPurposes { get; } =
    [new("プロジェクト全体の理解", PromptPurpose.UnderstandProject), new("特定機能の解析", PromptPurpose.AnalyzeFeature),
     new("既存機能の改善案", PromptPurpose.ImproveFeature), new("新機能の影響範囲", PromptPurpose.AssessImpact),
     new("リファクタリング計画", PromptPurpose.Refactor), new("不具合の原因調査", PromptPurpose.InvestigateBug)];
    public IReadOnlyList<Choice<string>> ExportFormats { get; } = [new("Markdown (.md)", ".md"), new("テキスト (.txt)", ".txt")];
    public IReadOnlyList<Choice<RelatedFileDirection>> RelatedDirections { get; } =
    [new("選択したファイル・型のみ", RelatedFileDirection.None), new("参照先を補足", RelatedFileDirection.Outgoing),
     new("参照元を補足", RelatedFileDirection.Incoming), new("両方向を補足", RelatedFileDirection.Both)];
    public IReadOnlyList<Choice<int>> RelatedDepths { get; } = [new("1段", 1), new("2段", 2), new("3段", 3)];

    public ObservableCollection<TreeNode> TreeRoots { get; } = [];
    public ObservableCollection<FileItem> FileItems { get; } = [];
    public ObservableCollection<TypeItem> TypeItems { get; } = [];
    public ObservableCollection<MemberItem> MemberItems { get; } = [];
    public ObservableCollection<ProjectItem> ProjectItems { get; } = [];
    public ObservableCollection<RelationshipItem> RelationshipItems { get; } = [];
    public ObservableCollection<AnalysisDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<ContextPart> Parts { get; } = [];
    public ICollectionView TypesView { get; }
    public ICollectionView MembersView { get; }
    public ICollectionView FilesView { get; }
    public ICollectionView ProjectsView { get; }
    public ICollectionView RelationshipsView { get; }
    public ICollectionView DiagnosticsView { get; }

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand BrowseFileCommand { get; }
    public AsyncCommand AnalyzeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand GenerateCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public RelayCommand SelectVisibleFilesCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    public bool IsBusy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { Raise(nameof(CanEdit)); Raise(nameof(CanChooseRelatedDepth)); RefreshCommands(); } }
    }
    public bool CanEdit => !IsBusy;
    public bool ProgressIsIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public double ProgressValue { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public string ErrorText { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public string InputPath { get => _inputPath; set { if (Set(ref _inputPath, value)) { MarkAnalysisDirty(); AnalyzeCommand?.Refresh(); } } }
    public string ExcludedDirectories { get => _excludedDirectories; set { if (Set(ref _excludedDirectories, value)) MarkAnalysisDirty(); } }
    public string IncludedRelativePaths { get => _includedRelativePaths; set { if (Set(ref _includedRelativePaths, value)) MarkAnalysisDirty(); } }
    public string FileFilter { get => _fileFilter; set { if (Set(ref _fileFilter, value)) BuildTree(); } }
    public string ResultsFilter
    {
        get => _resultsFilter;
        set
        {
            if (!Set(ref _resultsFilter, value)) return;
            foreach (var view in new[] { TypesView, MembersView, FilesView, ProjectsView, RelationshipsView, DiagnosticsView }) view.Refresh();
        }
    }
    public Choice<ContextKind> SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (!Set(ref _selectedKind, value)) return;
            Raise(nameof(IsFeatureContext));
            Raise(nameof(CanChooseRelatedDepth));
            InvalidatePreview();
        }
    }
    public bool IsFeatureContext => SelectedKind.Value == ContextKind.Feature;
    public bool CanChooseRelatedDepth => CanEdit && IsFeatureContext && SelectedRelatedDirection.Value != RelatedFileDirection.None;
    public Choice<RelatedFileDirection> SelectedRelatedDirection
    {
        get => _selectedRelatedDirection;
        set { if (Set(ref _selectedRelatedDirection, value)) { Raise(nameof(CanChooseRelatedDepth)); InvalidatePreview(); } }
    }
    public Choice<int> SelectedRelatedDepth { get => _selectedRelatedDepth; set { if (Set(ref _selectedRelatedDepth, value)) InvalidatePreview(); } }
    public Choice<PromptPurpose> SelectedPurpose { get => _selectedPurpose; set { if (Set(ref _selectedPurpose, value)) InvalidatePreview(); } }
    public string Request { get => _request; set { if (Set(ref _request, value)) InvalidatePreview(); } }
    public bool IncludeSource { get => _includeSource; set { if (Set(ref _includeSource, value)) InvalidatePreview(); } }
    public string PartSizeText { get => _partSizeText; set { if (Set(ref _partSizeText, value)) InvalidatePreview(); } }
    public string OutputDirectory { get => _outputDirectory; set { if (Set(ref _outputDirectory, value)) ExportCommand.Refresh(); } }
    public Choice<string> SelectedFormat { get => _selectedFormat; set => Set(ref _selectedFormat, value); }
    public FileItem? SelectedFile { get => _selectedFile; set => Set(ref _selectedFile, value); }
    public ContextPart? SelectedPart
    {
        get => _selectedPart;
        set { if (Set(ref _selectedPart, value)) { Raise(nameof(PreviewText)); Raise(nameof(PartCharacters)); CopyCommand?.Refresh(); } }
    }
    public string PreviewText => SelectedPart?.Text ?? "";
    public string PreviewHint { get => _previewHint; private set => Set(ref _previewHint, value); }
    public string ExportedFiles { get => _exportedFiles; private set => Set(ref _exportedFiles, value); }
    public string ProjectName => _result?.ProjectName ?? "未読み込み";
    public string RootPath => _result?.RootPath ?? "";
    public string AnalysisState => _result is null ? "解析範囲は空欄で全体、相対パスを ; 区切りで指定して絞り込み" : _analysisDirty ? "解析条件が変更されました。資料生成には再解析が必要です。" : _result.RootPath;
    public int ProjectCount => _result?.Projects.Count ?? 0;
    public int FileCount => _result?.Files.Count ?? 0;
    public int ClassCount => _result?.ClassCount ?? 0;
    public int MethodCount => _result?.MethodCount ?? 0;
    public int TypeCount => TypeItems.Count;
    public int DiagnosticCount => Diagnostics.Count;
    public string SelectedScopeText => $"ファイル {FileItems.Count(f => f.InContext):N0} / 型 {TypeItems.Count(t => t.InContext):N0} を選択";
    public string ContextCharacters => _document is null ? "未生成" : $"全体 {_document.CharacterCount:N0} 文字 · {_document.Parts.Count:N0} パート";
    public string PartCharacters => SelectedPart is null ? "" : $"表示中 {SelectedPart.Text.Length:N0} 文字";

    private async Task AnalyzeAsync()
    {
        BeginOperation("解析対象を確認しています…");
        ClearResults();
        var operation = _operation!;
        try
        {
            var options = new AnalysisOptions
            {
                InputPath = InputPath.Trim(),
                ExcludedDirectories = SplitList(ExcludedDirectories),
                IncludedRelativePaths = SplitList(IncludedRelativePaths)
            };
            var progress = new Progress<AnalysisProgress>(p =>
            {
                if (_operation != operation) return;
                ProgressIsIndeterminate = p.Total <= 0;
                ProgressValue = p.Total > 0 ? 100.0 * p.Completed / p.Total : 0;
                StatusText = p.Total > 0 ? $"{p.Completed:N0} / {p.Total:N0} · {p.Message}" : p.Message;
            });
            // Roslynの処理をUIスレッドから外し、数百ファイルでも画面と取消操作を応答させる。
            var result = await Task.Run(() => _analyzer.AnalyzeAsync(options, progress, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            SetResult(result);
            ProgressValue = 100;
            StatusText = $"解析完了 · {FileCount:N0} ファイル / {TypeCount:N0} 型 / {MethodCount:N0} メソッド・コンストラクター / {DiagnosticCount:N0} 診断";
            PreviewHint = "資料の種類と用途を選び、「資料を生成」を押してください。";
        }
        catch (OperationCanceledException) { StatusText = "解析をキャンセルしました。結果は保存していません。"; }
        catch (Exception ex) { ShowError("解析できませんでした", ex); }
        finally { EndOperation(); }
    }

    private async Task GenerateAsync()
    {
        if (_result is null || _analysisDirty) return;
        try
        {
            if (!int.TryParse(PartSizeText, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var maxCharacters)
                || maxCharacters < 1000 || maxCharacters > 100000)
                throw new ArgumentException("1パートの文字数は 1,000 ～ 100,000 の整数で指定してください。");
            var options = new ContextOptions
            {
                Kind = SelectedKind.Value,
                Purpose = SelectedPurpose.Value,
                SelectedFiles = FileItems.Where(f => f.InContext).Select(f => f.RelativePath).ToArray(),
                SelectedTypes = TypeItems.Where(t => t.InContext).Select(t => t.FullName).Distinct().ToArray(),
                RelatedDirection = SelectedRelatedDirection.Value,
                RelatedDepth = SelectedRelatedDepth.Value,
                IncludeSource = IncludeSource,
                Request = Request.Trim(),
                MaxPartCharacters = maxCharacters
            };
            if (options.Kind == ContextKind.Feature && options.SelectedFiles.Count == 0 && options.SelectedTypes.Count == 0)
                throw new ArgumentException("機能別資料では、ツリーまたは一覧のチェックボックスで対象ファイル・型を選択してください。");
            if (options.IncludeSource && options.SelectedFiles.Count == 0 && options.SelectedTypes.Count == 0)
                throw new ArgumentException("ソース本文を含める場合は、対象ファイルまたは型を選択してください。");

            // 役割メモはメモリ内の解析結果に反映する。解析対象のファイルは変更しない。
            var annotatedResult = _result with { Files = FileItems.Select(f => f.File with { RoleHint = f.RoleNote }).ToArray() };
            InvalidatePreview();
            BeginOperation("Copilot用の資料を生成しています…");
            var operation = _operation!;
            try
            {
                var document = await Task.Run(() => _generator.Generate(annotatedResult, options, operation.Token), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _document = document;
                foreach (var part in document.Parts) Parts.Add(part);
                SelectedPart = Parts.FirstOrDefault();
                Raise(nameof(ContextCharacters));
                PreviewHint = $"{document.ProjectName} · {document.Scope}";
                StatusText = $"資料を生成しました。{ContextCharacters}。コピー・保存はボタン操作時のみ実行します。";
                RefreshCommands();
            }
            catch (OperationCanceledException) { StatusText = "資料の生成をキャンセルしました。"; }
            catch (Exception ex) { ShowError("資料を生成できませんでした", ex); }
            finally { EndOperation(); }
        }
        catch (Exception ex) { ShowError("生成条件を確認してください", ex); }
    }

    private async Task ExportAsync()
    {
        if (_result is null || _document is null) return;
        BeginOperation("指定したフォルダーに資料を保存しています…");
        try
        {
            var files = await _exporter.ExportAsync(_document, OutputDirectory.Trim(), SelectedFormat.Value, _result.RootPath, _operation!.Token);
            ExportedFiles = string.Join(Environment.NewLine, files);
            StatusText = $"{files.Count:N0} ファイルを保存しました · {OutputDirectory.Trim()}";
        }
        catch (OperationCanceledException) { StatusText = "保存をキャンセルしました。保存先を確認してください。"; }
        catch (Exception ex) { ShowError("資料を保存できませんでした", ex); }
        finally { EndOperation(); }
    }

    private void CopySelectedPart()
    {
        if (SelectedPart is null) return;
        _interaction.CopyText(SelectedPart.Text);
        StatusText = $"パート {SelectedPart.Number} / {SelectedPart.Total} をコピーしました · {SelectedPart.Text.Length:N0} 文字";
    }

    private void BeginOperation(string message)
    {
        ErrorText = "";
        _operation = new CancellationTokenSource();
        ProgressValue = 0;
        ProgressIsIndeterminate = true;
        StatusText = message;
        IsBusy = true;
    }

    private void EndOperation()
    {
        _operation?.Dispose();
        _operation = null;
        ProgressIsIndeterminate = false;
        IsBusy = false;
    }

    private void Cancel()
    {
        _operation?.Cancel();
        StatusText = "キャンセルしています…";
        CancelCommand.Refresh();
    }

    private void SetResult(AnalysisResult result)
    {
        _result = result;
        _analysisDirty = false;
        foreach (var file in result.Files)
        {
            FileItems.Add(new FileItem(file, SelectionOrNotesChanged));
            foreach (var type in file.Types)
            {
                TypeItems.Add(new TypeItem(type, SelectionOrNotesChanged));
                foreach (var member in type.Members) MemberItems.Add(new MemberItem(type.FullName, file.RelativePath, member));
            }
        }
        foreach (var project in result.Projects) ProjectItems.Add(new ProjectItem(project));
        foreach (var relationship in result.Relationships) RelationshipItems.Add(new RelationshipItem(relationship));
        foreach (var diagnostic in result.Diagnostics) Diagnostics.Add(diagnostic);
        BuildTree();
        RaiseResultProperties();
        RefreshCommands();
    }

    private void ClearResults()
    {
        _result = null;
        _analysisDirty = true;
        SelectedFile = null;
        foreach (var collection in new[] { TreeRoots }) collection.Clear();
        FileItems.Clear(); TypeItems.Clear(); MemberItems.Clear(); ProjectItems.Clear(); RelationshipItems.Clear(); Diagnostics.Clear();
        InvalidatePreview();
        RaiseResultProperties();
    }

    private void RaiseResultProperties()
    {
        foreach (var property in new[] { nameof(ProjectName), nameof(RootPath), nameof(AnalysisState), nameof(ProjectCount), nameof(FileCount), nameof(ClassCount), nameof(MethodCount), nameof(TypeCount), nameof(DiagnosticCount), nameof(SelectedScopeText) }) Raise(property);
    }

    private void SelectionOrNotesChanged()
    {
        Raise(nameof(SelectedScopeText));
        InvalidatePreview();
    }

    private void MarkAnalysisDirty()
    {
        _analysisDirty = true;
        InvalidatePreview();
        Raise(nameof(AnalysisState));
        if (_result is not null) StatusText = "解析条件を変更しました。再度「解析を実行」を押してください。";
    }

    private bool FileMatches(FileItem file) => Matches(FileFilter, file.RelativePath, file.ProjectName, file.RoleNote);
    private static bool Matches(string filter, params string?[] values) => string.IsNullOrWhiteSpace(filter)
        || values.Any(value => value?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    private void BuildTree()
    {
        TreeRoots.Clear();
        if (_result is null) return;
        var root = new TreeNode(_result.ProjectName, _result.RootPath);
        foreach (var file in FileItems.Where(FileMatches))
        {
            var segments = file.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            var path = "";
            for (var i = 0; i < segments.Length; i++)
            {
                path = path.Length == 0 ? segments[i] : path + "/" + segments[i];
                var isFile = i == segments.Length - 1;
                var node = isFile ? null : parent.Children.FirstOrDefault(n => !n.HasFile && n.Name == segments[i]);
                if (node is null)
                {
                    node = new TreeNode(segments[i], path, isFile ? file : null);
                    parent.Children.Add(node);
                }
                parent = node;
            }
        }
        TreeRoots.Add(root);
    }

    // 条件変更後に古い資料を誤ってコピーしないよう、プレビューとコピー・保存の可否を同時に更新する。
    private void InvalidatePreview()
    {
        _document = null;
        SelectedPart = null;
        Parts.Clear();
        ExportedFiles = "";
        PreviewHint = _result is null ? "解析結果から資料を生成すると、ここに表示されます。" : "条件を変更しました。「資料を生成」でプレビューを更新してください。";
        Raise(nameof(ContextCharacters));
        RefreshCommands();
    }

    private void RunAction(Action action)
    {
        ErrorText = "";
        try { action(); }
        catch (Exception ex) { ShowError("操作できませんでした", ex); }
    }

    private void ShowError(string operation, Exception ex)
    {
        ErrorText = $"{operation}：{ex.Message}";
        StatusText = "処理を完了できませんでした。下のエラー内容を確認してください。";
    }

    private void RefreshCommands()
    {
        BrowseFolderCommand?.Refresh(); BrowseFileCommand?.Refresh(); AnalyzeCommand?.Refresh(); CancelCommand?.Refresh();
        GenerateCommand?.Refresh(); CopyCommand?.Refresh(); BrowseOutputCommand?.Refresh(); ExportCommand?.Refresh();
        SelectVisibleFilesCommand?.Refresh(); ClearSelectionCommand?.Refresh();
    }

    private static string[] SplitList(string value) => value.Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // 起動検証専用。画面の初期化、実際の解析、資料の表示までを確認し、保存やコピーは実行しない。
    public async Task RunSmokeTestAsync(string inputPath)
    {
        InputPath = inputPath;
        await AnalyzeAsync();
        if (_result is null || FileCount == 0 || HasError) throw new InvalidOperationException(ErrorText.Length > 0 ? ErrorText : "スモークテストの解析結果が空です。");
        SelectedKind = ContextKinds[1];
        await GenerateAsync();
        if (_document is null || string.IsNullOrWhiteSpace(PreviewText) || HasError) throw new InvalidOperationException("スモークテストで資料を生成できませんでした。");
        if (!CopyCommand.CanExecute(null)) throw new InvalidOperationException("生成後の明示コピーが有効になっていません。");

        // ソース含有条件や選択を変えたとき、古い資料がコピー・保存できないことを起動時検証でも確認する。
        IncludeSource = true;
        AssertPreviewInvalidated();
        IncludeSource = false;
        await GenerateAsync();
        FileItems[0].InContext = true;
        AssertPreviewInvalidated();
        await GenerateAsync();
        var originalScope = IncludedRelativePaths;
        IncludedRelativePaths = "__smoke_scope_changed__";
        AssertPreviewInvalidated();
        if (GenerateCommand.CanExecute(null)) throw new InvalidOperationException("解析範囲変更後に古い解析結果から資料を生成できてしまいます。");
        IncludedRelativePaths = originalScope;
        await AnalyzeAsync();
        await GenerateAsync();
        if (_document is null || HasError) throw new InvalidOperationException("条件変更の検証後に資料を再生成できませんでした。");

        // 関連範囲は生成条件。方向・段数の変更でコピーと保存を無効化し、再生成で復帰させる。
        var originalOutput = OutputDirectory;
        OutputDirectory = Path.Combine(Path.GetTempPath(), "ProjectLens.Smoke.Export");
        SelectedKind = ContextKinds[2];
        FileItems[0].InContext = true;
        await GenerateAsync();
        AssertFeaturePreviewGenerated();
        SelectedRelatedDirection = RelatedDirections[1];
        AssertPreviewInvalidated();
        await GenerateAsync();
        AssertFeaturePreviewGenerated();
        SelectedRelatedDepth = RelatedDepths[1];
        AssertPreviewInvalidated();
        await GenerateAsync();
        AssertFeaturePreviewGenerated();
        SelectedRelatedDirection = RelatedDirections[0];
        AssertPreviewInvalidated();
        if (CanChooseRelatedDepth) throw new InvalidOperationException("選択のみで関連の段数を変更できてしまいます。");
        await GenerateAsync();
        AssertFeaturePreviewGenerated();
        SelectedRelatedDirection = RelatedDirections[3];
        await GenerateAsync();
        AssertFeaturePreviewGenerated();
        OutputDirectory = originalOutput;
    }

    private void AssertFeaturePreviewGenerated()
    {
        if (_document is null || HasError || !PreviewText.Contains("選択範囲と関連ファイル", StringComparison.Ordinal)
            || !CopyCommand.CanExecute(null) || !ExportCommand.CanExecute(null))
            throw new InvalidOperationException("関連範囲の設定後に資料・コピー・保存を利用できません。" + ErrorText);
    }

    private void AssertPreviewInvalidated()
    {
        if (PreviewText.Length != 0 || CopyCommand.CanExecute(null) || ExportCommand.CanExecute(null))
            throw new InvalidOperationException("条件変更後に古い資料が有効のままです。");
    }

    public void CancelActiveOperation() => _operation?.Cancel();
}
