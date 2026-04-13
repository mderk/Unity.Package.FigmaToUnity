using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        #endregion

        #region Fields
        string fileKey;
        string outputFolder;
        string username;
        bool resolvingName;
        bool fetching;
        bool importing;

        string cachedFileKey;
        string cachedJson;

        string statusMessage;
        MessageType statusType;

        List<FrameInfo> frames = new();
        Vector2 scrollPosition;
        string searchBar = "";
        #endregion

        #region Types
        class FrameInfo
        {
            public string pageName;
            public string frameName;
            public string path;
            public bool selected;
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
        }

        void OnGUI()
        {
            DrawTokenSection();

            if (PersonalAccessToken.NullOrEmpty())
                return;

            DrawFileKeySection();
            DrawOutputSection();
            DrawStatusMessage();
            DrawFramesList();
            DrawImportButtons();
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
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                fileKey = EditorGUILayout.TextField("File Key", fileKey);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(fileKeyPrefKey, fileKey);

                bool hasCachedFrames = frames.Count > 0 && cachedFileKey == fileKey;

                using (new EditorGUI.DisabledScope(fileKey.NullOrEmpty() || fetching))
                {
                        if (hasCachedFrames)
                    {
                        if (GUILayout.Button(fetching ? "Fetching..." : "Refresh", GUILayout.Width(100)))
                            FetchPages(forceRefresh: true);
                    }
                    else
                    {
                        if (GUILayout.Button(fetching ? "Fetching..." : "Fetch Pages", GUILayout.Width(100)))
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
        }

        void DrawFramesList()
        {
            if (frames.Count == 0)
            {
                if (statusMessage.NullOrEmpty())
                    EditorGUILayout.HelpBox("Click 'Fetch Pages' to load the document structure from Figma.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Frames ({frames.Count(f => f.selected)}/{frames.Count} selected)", EditorStyles.boldLabel);

            searchBar = EditorGUILayout.TextField(searchBar, EditorStyles.toolbarSearchField);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(80)))
                    frames.ForEach(f => f.selected = true);
                if (GUILayout.Button("Select None", GUILayout.Width(80)))
                    frames.ForEach(f => f.selected = false);
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            string currentPage = null;
            foreach (FrameInfo frame in frames)
            {
                if (!string.IsNullOrWhiteSpace(searchBar) && !frame.path.ToLower().Contains(searchBar.ToLower()))
                    continue;

                if (frame.pageName != currentPage)
                {
                    currentPage = frame.pageName;
                    EditorGUILayout.LabelField(currentPage, EditorStyles.miniLabel);
                }

                frame.selected = EditorGUILayout.ToggleLeft($"  {frame.frameName}", frame.selected);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawImportButtons()
        {
            if (frames.Count == 0 || !frames.Any(f => f.selected))
                return;

            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(importing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(importing ? "Importing..." : "Import", GUILayout.Height(28)))
                        RunImport(false);
                    if (GUILayout.Button(importing ? "Importing..." : "Import with Images", GUILayout.Height(28)))
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

        // ReSharper disable once AsyncVoidMethod
        async void FetchPages(bool forceRefresh = false)
        {
            fetching = true;
            ClearStatus();

            try
            {
                string json;

                if (!forceRefresh && cachedFileKey == fileKey && cachedJson.NotNullOrEmpty())
                {
                    json = cachedJson;
                }
                else
                {
                    using FigmaDownloader downloader = new(PersonalAccessToken, fileKey,
                        new AssetsInfo(Application.dataPath, "Assets", "temp", Array.Empty<string>()));

                    json = await downloader.FetchShallowAsync(CancellationToken.None);
                    cachedFileKey = fileKey;
                    cachedJson = json;
                }

                Data data = await Task.Run(() => JsonUtility.FromJson<Data>(json));

                frames.Clear();
                foreach (CanvasNode page in data.document.children)
                {
                    if (page.children == null) continue;
                    foreach (SceneNode frame in page.children)
                    {
                        frames.Add(new FrameInfo
                        {
                            pageName = page.name,
                            frameName = frame.name,
                            path = $"{page.name}/{frame.name}",
                            selected = false
                        });
                    }
                }

                SetStatus($"Fetched {frames.Count} frames from {data.document.children.Length} pages.", MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus($"Failed to fetch: {e.Message}", MessageType.Error);
                Debug.LogError($"[FigmaImport] Failed to fetch pages: {e.Message}");
            }
            finally
            {
                fetching = false;
                Repaint();
            }
        }

        // ReSharper disable once AsyncVoidMethod
        async void RunImport(bool downloadImages)
        {
            importing = true;
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

            using CancellationTokenSource cts = new();

            try
            {
                Progress.RegisterCancelCallback(progress, () =>
                {
                    cts.Cancel();
                    return true;
                });

                AssetDatabase.StartAssetEditing();

                AssetsInfo info = new(absoluteOutputFolder, outputFolder, uxmlName, Array.Empty<string>());
                using FigmaDownloader downloader = new(PersonalAccessToken, fileKey, info);

                await downloader.Run(downloadImages, uxmlName, selectedPaths, true, progress, cts.Token);
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
                Progress.UnregisterCancelCallback(progress);
                stopwatch.Reset();
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                importing = false;
                Repaint();
            }
        }
        #endregion
    }
}
