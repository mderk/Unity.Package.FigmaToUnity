using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Figma.Inspectors
{
    using Core.Assets;
    using Internals;
    using static Styles;

    public class FigmaImportWindow : EditorWindow
    {
        #region Consts
        static readonly string patKey = $"{nameof(Figma)}/{nameof(PersonalAccessToken)}";
        const string fileKeyPrefKey = "Figma/ImportWindow/FileKey";
        const string outputFolderPrefKey = "Figma/ImportWindow/OutputFolder";
        const string recentKeysPrefKey = "Figma/ImportWindow/RecentKeys";
        const string selectedFramesPrefKey = "Figma/ImportWindow/SelectedFrames/";
        const int maxRecentKeys = 10;
        const int thumbnailHeight = 48;

        static readonly Regex figmaUrlPattern = new(@"figma\.com/(?:file|design)/([a-zA-Z0-9]+)", RegexOptions.Compiled);

        static string CacheDirectory => Path.Combine(Directory.GetCurrentDirectory(), "Library", "FigmaCache");
        static string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbnails");
        #endregion

        #region Fields
        string fileKey;
        string outputFolder;
        string username;
        bool resolvingName;
        bool fetching;
        bool importing;

        string documentName;
        DateTime cacheDate;

        string statusMessage;
        MessageType statusType;

        string importProgressLabel;
        string fetchProgressLabel;
        Stopwatch fetchStopwatch;
        CancellationTokenSource fetchCts;
        CancellationTokenSource importCts;

        List<FrameInfo> frames = new();
        List<RecentFile> recentFiles = new();
        Vector2 scrollPosition;
        string searchBar = "";
        bool thumbnailsLoading;
        Vector2 selectedScrollPosition;
        readonly List<(FrameInfo frame, string pageName)> visibleRows = new();
        #endregion

        #region Types
        class FrameInfo
        {
            public string nodeId;
            public string pageName;
            public string frameName;
            public string path;
            public bool selected;
            public Texture2D thumbnail;
        }

        [Serializable]
        class CacheEntry
        {
            public string json;
            public string documentName;
            public string cachedAt;
        }

        class RecentFile
        {
            public string fileKey;
            public string name;
        }
        #endregion

        #region Properties
        static string PersonalAccessToken
        {
            get => EditorPrefs.GetString(patKey, string.Empty);
            set => EditorPrefs.SetString(patKey, value);
        }
        #endregion

        #region Menu
        [MenuItem("Tools/Figma Import")]
        static void ShowWindow()
        {
            FigmaImportWindow window = GetWindow<FigmaImportWindow>("Figma Import");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }
        #endregion

        #region Methods
        void OnEnable()
        {
            fileKey = EditorPrefs.GetString(fileKeyPrefKey, "");
            outputFolder = EditorPrefs.GetString(outputFolderPrefKey, "Assets/FigmaImport");
            LoadRecentFiles();

            if (fileKey.NotNullOrEmpty() && TryLoadFromDiskCache(fileKey))
            {
                RestoreSelectedFrames();
                LoadCachedThumbnails();
            }
        }

        void OnDisable()
        {
            foreach (FrameInfo frame in frames)
                if (frame.thumbnail != null)
                    DestroyImmediate(frame.thumbnail);
        }

        void OnGUI()
        {
            DrawTokenSection();

            if (PersonalAccessToken.NullOrEmpty())
                return;

            DrawFileKeySection();
            DrawOutputSection();
            DrawStatusMessage();
            DrawImportButtons();
            DrawFramesList();
        }

        void DrawTokenSection()
        {
            // ReSharper disable once AsyncVoidMethod
            async void GetNameAsync(string personalAccessToken)
            {
                resolvingName = true;
                using AuthTest auth = new(personalAccessToken);
                await auth.AuthAsync();

                if (!auth.IsAuthenticated)
                    return;

                username = auth.me.handle;
                PersonalAccessToken = personalAccessToken;
                resolvingName = false;
                Repaint();
            }

            using (new EditorGUILayout.HorizontalScope(GUI.skin.box))
            {
                if (PersonalAccessToken.NotNullOrEmpty())
                {
                    if (username.NullOrEmpty() && !resolvingName)
                        GetNameAsync(PersonalAccessToken);

                    EditorGUILayout.LabelField(EditorGUIUtility.TrIconContent(LoggedInIcon), GUILayout.Width(20));
                    EditorGUILayout.LabelField("Logged in as", GUILayout.Width(72));
                    EditorGUILayout.LabelField(username, EditorStyles.boldLabel);

                    if (GUILayout.Button("Log Out", GUILayout.Width(60)))
                    {
                        PersonalAccessToken = string.Empty;
                        username = null;
                    }
                }
                else
                {
                    EditorGUILayout.PrefixLabel("Figma PAT");
                    string token = EditorGUILayout.TextField(PersonalAccessToken);
                    if (GUI.changed)
                        GetNameAsync(token);
                }
            }
        }

        void DrawFileKeySection()
        {
            // File key input + dropdown
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                fileKey = EditorGUILayout.TextField("File Key", fileKey);
                if (EditorGUI.EndChangeCheck())
                {
                    fileKey = ExtractFileKey(fileKey);
                    EditorPrefs.SetString(fileKeyPrefKey, fileKey);
                    TryLoadFromDiskCache(fileKey);
                    RestoreSelectedFrames();
                    LoadCachedThumbnails();
                }

                if (recentFiles.Count > 0)
                {
                    if (GUILayout.Button("\u25BC", GUILayout.Width(20)))
                    {
                        GenericMenu menu = new();
                        foreach (RecentFile recent in recentFiles)
                        {
                            string label = recent.name.NotNullOrEmpty() ? $"{recent.name}  ({recent.fileKey})" : recent.fileKey;
                            string key = recent.fileKey;
                            menu.AddItem(new GUIContent(label), fileKey == key, () =>
                            {
                                fileKey = key;
                                EditorPrefs.SetString(fileKeyPrefKey, fileKey);
                                TryLoadFromDiskCache(fileKey);
                                RestoreSelectedFrames();
                                LoadCachedThumbnails();
                                Repaint();
                            });
                        }
                        menu.ShowAsContext();
                    }
                }
            }

            // Document name + cache info
            if (documentName.NotNullOrEmpty())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Document", documentName, EditorStyles.boldLabel);

                    if (cacheDate != default)
                    {
                        string age = FormatCacheAge(cacheDate);
                        EditorGUILayout.LabelField($"cached {age}", EditorStyles.miniLabel, GUILayout.Width(140));
                    }
                }
            }

            // Fetch progress
            if (fetching && fetchProgressLabel.NotNullOrEmpty())
                EditorGUILayout.LabelField(fetchProgressLabel, EditorStyles.miniLabel);

            // Fetch / Refresh / Cancel buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (fetching)
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(60)))
                        fetchCts?.Cancel();
                }

                bool hasCachedFrames = frames.Count > 0;

                using (new EditorGUI.DisabledScope(fileKey.NullOrEmpty() || fetching))
                {
                    if (hasCachedFrames)
                    {
                        if (GUILayout.Button(fetching ? "Fetching..." : "Refresh from Figma", GUILayout.Width(140)))
                            FetchPages(forceRefresh: true);
                    }
                    else
                    {
                        if (GUILayout.Button(fetching ? "Fetching..." : "Fetch Pages", GUILayout.Width(140)))
                            FetchPages(forceRefresh: false);
                    }
                }
            }
        }

        void DrawOutputSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(outputFolderPrefKey, outputFolder);

                if (GUILayout.Button("Browse", GUILayout.Width(60)))
                {
                    string selected = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, "FigmaImport");
                    if (selected.NotNullOrEmpty())
                    {
                        if (selected.StartsWith(Application.dataPath))
                            outputFolder = "Assets" + selected.Substring(Application.dataPath.Length);
                        else
                            outputFolder = selected;
                        EditorPrefs.SetString(outputFolderPrefKey, outputFolder);
                    }
                }
            }
        }

        void DrawStatusMessage()
        {
            if (statusMessage.NullOrEmpty())
                return;

            EditorGUILayout.HelpBox(statusMessage, statusType);

            // Show "Open Output" button after successful import
            if (statusType == MessageType.Info && statusMessage.Contains("Import completed"))
            {
                if (GUILayout.Button("Open Output Folder", GUILayout.Height(22)))
                {
                    UnityEngine.Object folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outputFolder);
                    if (folder != null)
                    {
                        EditorGUIUtility.PingObject(folder);
                        Selection.activeObject = folder;
                    }
                }
            }
        }

        void DrawFramesList()
        {
            if (frames.Count == 0)
            {
                if (!fetching && statusMessage.NullOrEmpty())
                    EditorGUILayout.HelpBox("Click 'Fetch Pages' to load the document structure from Figma.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);



            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Frames ({frames.Count(f => f.selected)}/{frames.Count} selected)", EditorStyles.boldLabel);

                if (thumbnailsLoading)
                    EditorGUILayout.LabelField("loading thumbnails...", EditorStyles.miniLabel, GUILayout.Width(120));
            }

            searchBar = EditorGUILayout.TextField(searchBar, EditorStyles.toolbarSearchField);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(80)))
                {
                    frames.ForEach(f => f.selected = true);
                    SaveSelectedFrames();
                }
                if (GUILayout.Button("Select None", GUILayout.Width(80)))
                {
                    frames.ForEach(f => f.selected = false);
                    SaveSelectedFrames();
                }
            }

            // Selected frames summary
            List<FrameInfo> selectedFrames = frames.Where(f => f.selected).ToList();
            if (selectedFrames.Count > 0)
            {
                EditorGUILayout.LabelField($"Selected ({selectedFrames.Count})", EditorStyles.miniLabel);
                float selectedHeight = Mathf.Min(selectedFrames.Count * (EditorGUIUtility.singleLineHeight + 2), 120);
                selectedScrollPosition = EditorGUILayout.BeginScrollView(selectedScrollPosition, GUILayout.Height(selectedHeight + 8));
                foreach (FrameInfo sf in selectedFrames)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(sf.path, EditorStyles.miniLabel);
                        if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(20)))
                        {
                            sf.selected = false;
                            SaveSelectedFrames();
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }

            // Separator
            EditorGUILayout.Space(2);
            UnityEngine.Rect sepRect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(sepRect, new Color(0.3f, 0.3f, 0.3f, 1f));
            EditorGUILayout.Space(2);

            EditorGUILayout.LabelField("All Frames", EditorStyles.miniLabel);

            bool hasThumbnails = frames.Any(f => f.thumbnail != null);
            float rowHeight = hasThumbnails ? thumbnailHeight + 4 : EditorGUIUtility.singleLineHeight + 2;

            // Build visible rows: interleave page headers and frames
            bool hasSearch = !string.IsNullOrWhiteSpace(searchBar);
            string searchLower = hasSearch ? searchBar.ToLower() : null;
            visibleRows.Clear();

            string currentPage = null;
            foreach (FrameInfo frame in frames)
            {
                if (hasSearch && !frame.path.ToLower().Contains(searchLower))
                    continue;

                if (frame.pageName != currentPage)
                {
                    currentPage = frame.pageName;
                    visibleRows.Add((null, currentPage));
                }
                visibleRows.Add((frame, null));
            }

            int totalRows = visibleRows.Count;
            float totalHeight = totalRows * rowHeight;

            // Use GUILayoutUtility to get the remaining area, then manual scroll view
            UnityEngine.Rect viewRect = GUILayoutUtility.GetRect(0, 10000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            UnityEngine.Rect contentRect = new(0, 0, viewRect.width - 14, totalHeight);
            scrollPosition = GUI.BeginScrollView(viewRect, scrollPosition, contentRect);

            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / rowHeight));
            int lastVisible = Mathf.Min(totalRows, Mathf.CeilToInt((scrollPosition.y + viewRect.height) / rowHeight) + 1);

            for (int i = firstVisible; i < lastVisible; i++)
            {
                UnityEngine.Rect rowRect = new(0, i * rowHeight, contentRect.width, rowHeight);
                (FrameInfo frame, string pageName) = visibleRows[i];

                if (frame == null)
                {
                    // Page header
                    UnityEngine.Rect labelRect = new(rowRect.x, rowRect.y, rowRect.width - 36, rowRect.height);
                    EditorGUI.LabelField(labelRect, pageName, EditorStyles.miniLabel);

                    UnityEngine.Rect btnRect = new(rowRect.xMax - 34, rowRect.y, 32, rowRect.height);
                    string page = pageName;
                    List<FrameInfo> pageFrames = frames.Where(f => f.pageName == page).ToList();
                    bool allSelected = pageFrames.All(f => f.selected);

                    if (GUI.Button(btnRect, allSelected ? "none" : "all", EditorStyles.miniButton))
                    {
                        bool newState = !allSelected;
                        pageFrames.ForEach(f => f.selected = newState);
                        SaveSelectedFrames();
                    }
                }
                else
                {
                    float xOffset = rowRect.x;

                    // Thumbnail
                    if (hasThumbnails && frame.thumbnail != null)
                    {
                        float aspect = (float)frame.thumbnail.width / frame.thumbnail.height;
                        float thumbWidth = thumbnailHeight * aspect;
                        UnityEngine.Rect thumbRect = new(xOffset, rowRect.y, thumbWidth, thumbnailHeight);
                        GUI.DrawTexture(thumbRect, frame.thumbnail, UnityEngine.ScaleMode.ScaleToFit);
                        xOffset += thumbWidth + 4;
                    }

                    // Checkbox
                    UnityEngine.Rect toggleRect = new(xOffset, rowRect.y, rowRect.xMax - xOffset, rowRect.height);
                    EditorGUI.BeginChangeCheck();
                    frame.selected = EditorGUI.ToggleLeft(toggleRect, frame.frameName, frame.selected);
                    if (EditorGUI.EndChangeCheck())
                        SaveSelectedFrames();
                }
            }

            GUI.EndScrollView();
        }

        void DrawImportButtons()
        {
            if (frames.Count == 0 || !frames.Any(f => f.selected))
                return;

            EditorGUILayout.Space(4);

            if (importing)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    UnityEngine.Rect r = EditorGUILayout.GetControlRect(false, 28);
                    EditorGUI.ProgressBar(r, -1f, importProgressLabel ?? "Importing...");

                    if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(60)))
                        importCts?.Cancel();
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Import", GUILayout.Height(28)))
                        RunImport(false);
                    if (GUILayout.Button("Import with Images", GUILayout.Height(28)))
                        RunImport(true);
                }
            }
        }
        #endregion

        #region Support Methods
        void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
            Repaint();
        }

        void ClearStatus()
        {
            statusMessage = null;
            Repaint();
        }

        static string FormatCacheAge(DateTime cacheTime)
        {
            TimeSpan age = DateTime.UtcNow - cacheTime;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
            return $"{(int)age.TotalDays}d ago";
        }

        static string ExtractFileKey(string input)
        {
            if (input == null) return input;
            Match match = figmaUrlPattern.Match(input);
            return match.Success ? match.Groups[1].Value : input;
        }

        #region Disk Cache
        static string GetCachePath(string key) => Path.Combine(CacheDirectory, $"{key}.json");

        void SaveToDiskCache(string key, string json, string docName)
        {
            Directory.CreateDirectory(CacheDirectory);
            CacheEntry entry = new() { json = json, documentName = docName, cachedAt = DateTime.UtcNow.ToString("O") };
            File.WriteAllText(GetCachePath(key), JsonUtility.ToJson(entry, false));
        }

        bool TryLoadFromDiskCache(string key)
        {
            frames.Clear();
            documentName = null;
            cacheDate = default;

            if (key.NullOrEmpty())
                return false;

            string path = GetCachePath(key);
            if (!File.Exists(path))
                return false;

            try
            {
                CacheEntry entry = JsonUtility.FromJson<CacheEntry>(File.ReadAllText(path));
                if (entry?.json == null)
                    return false;

                Data data = JsonUtility.FromJson<Data>(entry.json);
                PopulateFrames(data);

                documentName = entry.documentName;
                if (DateTime.TryParse(entry.cachedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                    cacheDate = parsed;

                SetStatus($"Loaded {frames.Count} frames from cache ({FormatCacheAge(cacheDate)}).", MessageType.Info);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FigmaImport] Failed to load cache: {e.Message}");
                return false;
            }
        }
        #endregion

        #region Thumbnails
        static string GetThumbnailPath(string fKey, string nodeId) =>
            Path.Combine(ThumbnailCacheDirectory, fKey, $"{nodeId.Replace(":", "_")}.png");

        void LoadCachedThumbnails()
        {
            foreach (FrameInfo frame in frames)
            {
                if (frame.thumbnail != null)
                    DestroyImmediate(frame.thumbnail);
                frame.thumbnail = null;

                string thumbPath = GetThumbnailPath(fileKey, frame.nodeId);
                if (!File.Exists(thumbPath)) continue;

                byte[] bytes = File.ReadAllBytes(thumbPath);
                Texture2D tex = new(2, 2) { filterMode = FilterMode.Bilinear };
                if (tex.LoadImage(bytes))
                    frame.thumbnail = tex;
                else
                    DestroyImmediate(tex);
            }
            Repaint();
        }

        // ReSharper disable once AsyncVoidMethod
        async void FetchThumbnails(CancellationToken token)
        {
            if (frames.Count == 0 || fileKey.NullOrEmpty()) return;

            // Only fetch thumbnails that aren't cached
            List<FrameInfo> missing = frames.Where(f => f.thumbnail == null && f.nodeId.NotNullOrEmpty()).ToList();
            if (missing.Count == 0) return;

            thumbnailsLoading = true;
            Repaint();

            try
            {
                using FigmaDownloader downloader = new(PersonalAccessToken, fileKey,
                    new AssetsInfo(Application.dataPath, "Assets", "temp", Array.Empty<string>()));

                Dictionary<string, string> urls = await downloader.FetchThumbnailsAsync(
                    missing.Select(f => f.nodeId), token);

                Directory.CreateDirectory(Path.Combine(ThumbnailCacheDirectory, fileKey));

                using HttpClient http = new();

                foreach (FrameInfo frame in missing)
                {
                    if (token.IsCancellationRequested) break;
                    if (!urls.TryGetValue(frame.nodeId, out string url) || string.IsNullOrEmpty(url)) continue;

                    try
                    {
                        byte[] bytes = await http.GetByteArrayAsync(url);
                        if (bytes.Length == 0) continue;

                        // Save to disk cache
                        string thumbPath = GetThumbnailPath(fileKey, frame.nodeId);
                        await File.WriteAllBytesAsync(thumbPath, bytes, token);

                        // Load into texture
                        Texture2D tex = new(2, 2) { filterMode = FilterMode.Bilinear };
                        if (tex.LoadImage(bytes))
                            frame.thumbnail = tex;
                        else
                            DestroyImmediate(tex);

                        Repaint();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[FigmaImport] Thumbnail failed for {frame.nodeId}: {e.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogWarning($"[FigmaImport] Thumbnail fetch failed: {e.Message}");
            }
            finally
            {
                thumbnailsLoading = false;
                Repaint();
            }
        }
        #endregion

        #region Selected Frames Persistence
        void SaveSelectedFrames()
        {
            if (fileKey.NullOrEmpty()) return;
            string selected = string.Join(";", frames.Where(f => f.selected).Select(f => f.path));
            EditorPrefs.SetString(selectedFramesPrefKey + fileKey, selected);
        }

        void RestoreSelectedFrames()
        {
            if (fileKey.NullOrEmpty() || frames.Count == 0) return;
            string raw = EditorPrefs.GetString(selectedFramesPrefKey + fileKey, "");
            if (raw.NullOrEmpty()) return;

            HashSet<string> selected = new(raw.Split(';'));
            foreach (FrameInfo frame in frames)
                frame.selected = selected.Contains(frame.path);
        }
        #endregion

        #region Recent Files
        void LoadRecentFiles()
        {
            recentFiles.Clear();
            string raw = EditorPrefs.GetString(recentKeysPrefKey, "");
            if (raw.NullOrEmpty()) return;

            foreach (string entry in raw.Split(';'))
            {
                string[] parts = entry.Split('|');
                if (parts.Length >= 1 && parts[0].NotNullOrEmpty())
                    recentFiles.Add(new RecentFile { fileKey = parts[0], name = parts.Length > 1 ? parts[1] : null });
            }
        }

        void AddRecentFile(string key, string name)
        {
            recentFiles.RemoveAll(r => r.fileKey == key);
            recentFiles.Insert(0, new RecentFile { fileKey = key, name = name });
            if (recentFiles.Count > maxRecentKeys)
                recentFiles.RemoveRange(maxRecentKeys, recentFiles.Count - maxRecentKeys);

            string serialized = string.Join(";", recentFiles.Select(r => $"{r.fileKey}|{r.name ?? ""}"));
            EditorPrefs.SetString(recentKeysPrefKey, serialized);
        }
        #endregion

        void PopulateFrames(Data data)
        {
            foreach (FrameInfo frame in frames)
                if (frame.thumbnail != null)
                    DestroyImmediate(frame.thumbnail);

            frames.Clear();
            foreach (CanvasNode page in data.document.children)
            {
                if (page.children == null) continue;
                foreach (SceneNode frame in page.children)
                {
                    frames.Add(new FrameInfo
                    {
                        nodeId = frame.id,
                        pageName = page.name,
                        frameName = frame.name,
                        path = $"{page.name}/{frame.name}",
                        selected = false
                    });
                }
            }
        }

        // ReSharper disable once AsyncVoidMethod
        async void FetchPages(bool forceRefresh = false)
        {
            fetching = true;
            ClearStatus();

            try
            {
                if (!forceRefresh && TryLoadFromDiskCache(fileKey))
                {
                    RestoreSelectedFrames();
                    LoadCachedThumbnails();
                    fetching = false;
                    Repaint();
                    return;
                }

                fetchStopwatch = Stopwatch.StartNew();
                fetchCts = new CancellationTokenSource();

                void PollFetch()
                {
                    if (!fetching) return;
                    fetchProgressLabel = $"Downloading document structure... {fetchStopwatch.Elapsed.TotalSeconds:F0}s";
                    Repaint();
                }

                EditorApplication.update += PollFetch;

                try
                {
                    fetchProgressLabel = "Connecting to Figma API...";
                    Repaint();

                    using FigmaDownloader downloader = new(PersonalAccessToken, fileKey,
                        new AssetsInfo(Application.dataPath, "Assets", "temp", Array.Empty<string>()));

                    string json = await downloader.FetchShallowAsync(fetchCts.Token, status =>
                    {
                        fetchProgressLabel = status;
                        Repaint();
                    });

                    fetchProgressLabel = "Parsing response...";
                    Repaint();

                    Data data = await Task.Run(() => JsonUtility.FromJson<Data>(json));
                    PopulateFrames(data);

                    documentName = data.name;
                    cacheDate = DateTime.UtcNow;

                    SaveToDiskCache(fileKey, json, documentName);
                    AddRecentFile(fileKey, documentName);
                    RestoreSelectedFrames();

                    SetStatus($"Fetched {frames.Count} frames from {data.document.children.Length} pages in {fetchStopwatch.Elapsed.TotalSeconds:F1}s.", MessageType.Info);

                    // Fetch thumbnails in background (disabled)
                    // FetchThumbnails(fetchCts.Token);
                }
                finally
                {
                    EditorApplication.update -= PollFetch;
                    fetchStopwatch.Stop();
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Fetch cancelled.", MessageType.Warning);
            }
            catch (Exception e)
            {
                SetStatus($"Failed to fetch: {e.Message}", MessageType.Error);
                Debug.LogError($"[FigmaImport] Failed to fetch pages: {e.Message}");
            }
            finally
            {
                fetching = false;
                fetchProgressLabel = null;
                fetchStopwatch = null;
                fetchCts?.Dispose();
                fetchCts = null;
                Repaint();
            }
        }

        // ReSharper disable once AsyncVoidMethod
        async void RunImport(bool downloadImages)
        {
            importing = true;
            importProgressLabel = "Starting import...";
            ClearStatus();

            List<string> selectedPaths = frames.Where(f => f.selected).Select(f => f.path).ToList();
            string uxmlName = "Figma";

            string absoluteOutputFolder = outputFolder.StartsWith("Assets")
                ? Path.Combine(Directory.GetCurrentDirectory(), outputFolder)
                : outputFolder;

            Directory.CreateDirectory(absoluteOutputFolder);

            Stopwatch stopwatch = Stopwatch.StartNew();
            string display = $"Figma Import" + (downloadImages ? " (Images)" : "");
            int progress = Progress.Start(display, null, Progress.Options.Managed);

            // Poll progress for inline display
            void PollProgress()
            {
                if (!importing) return;
                string desc = Progress.GetDescription(progress);
                string step = Progress.GetStepLabel(progress);
                importProgressLabel = desc.NotNullOrEmpty() ? desc : step.NotNullOrEmpty() ? step : importProgressLabel;
                Repaint();
            }

            EditorApplication.update += PollProgress;

            importCts = new CancellationTokenSource();

            try
            {
                Progress.RegisterCancelCallback(progress, () =>
                {
                    importCts?.Cancel();
                    return true;
                });

                AssetDatabase.StartAssetEditing();

                AssetsInfo info = new(absoluteOutputFolder, outputFolder, uxmlName, Array.Empty<string>());
                using FigmaDownloader downloader = new(PersonalAccessToken, fileKey, info);

                await downloader.Run(downloadImages, uxmlName, selectedPaths, true, progress, importCts.Token);
                downloader.CleanUp(downloadImages);
                downloader.RemoveEmptyDirectories();

                stopwatch.Stop();
                float seconds = (float)stopwatch.ElapsedMilliseconds / 1000;
                SetStatus($"Import completed in {seconds:F1}s — output: {outputFolder}", MessageType.Info);
                Debug.Log($"{display} is <color={SuccessColor}>completed</color> in {seconds}s — output: {outputFolder}");
                Progress.Finish(progress);
            }
            catch (Exception e)
            {
                Progress.Finish(progress, Progress.Status.Failed);

                if (e is OperationCanceledException)
                {
                    SetStatus("Import cancelled.", MessageType.Warning);
                }
                else
                {
                    SetStatus($"Import failed: {e.Message}", MessageType.Error);
                    Debug.LogException(e);
                }
            }
            finally
            {
                EditorApplication.update -= PollProgress;
                Progress.UnregisterCancelCallback(progress);
                stopwatch.Reset();
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                importing = false;
                importProgressLabel = null;
                importCts?.Dispose();
                importCts = null;
                Repaint();
            }
        }
        #endregion
    }
}
