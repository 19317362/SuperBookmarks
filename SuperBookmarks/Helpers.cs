﻿using System;
using System.IO;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using System.Linq;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Editor;
using System.Collections.Generic;
using System.Globalization;
using EnvDTE;
using Process = System.Diagnostics.Process;
using Microsoft.VisualStudio;

namespace Konamiman.SuperBookmarks
{
    static class Helpers
    {
        private static IServiceProvider serviceProvider = null;
        private static IVsStatusbar statusBar = null;

        private static IServiceProvider ServiceProvider =>
            serviceProvider ?? (serviceProvider = SuperBookmarksPackage.Instance);

        private static IVsStatusbar StatusBar =>
            statusBar ?? (statusBar = (IVsStatusbar)ServiceProvider.GetService(typeof(SVsStatusbar)));


        public static SimpleTagger<BookmarkTag> GetTaggerFor(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty("tagger", () => new SimpleTagger<BookmarkTag>(buffer));

        public static bool PathIsInGitRepository(string path) =>
            RunGitCommand("rev-parse --is-inside-work-tree", path) == "true";
        
        public static string GetGitRepositoryRoot(string path) =>
            RunGitCommand("rev-parse --show-toplevel", path);

        public static bool AddFileToGitignore(string gitignorePath, string fileToAdd, bool createGitignoreFile)
        {
            var relativeFileToAdd = fileToAdd.Substring(SuperBookmarksPackage.Instance.CurrentSolutionPath.Length);
            var lineToAdd = "\r\n# SuperBookmarks data file\r\n" + relativeFileToAdd + "\r\n";
            if (!File.Exists(gitignorePath))
            {
                if (!createGitignoreFile)
                    return false;

                File.WriteAllText(gitignorePath, lineToAdd);
                return true;
            }

            var gitignoreContents = File.ReadAllText(gitignorePath);
            if (Regex.IsMatch(gitignoreContents, $@"^\s*[^#]?{Regex.Escape(relativeFileToAdd)}\s*$", RegexOptions.Multiline))
                return false;

            File.AppendAllText(gitignorePath, lineToAdd);
            return true;
        }

        //http://stackoverflow.com/a/6119394/4574
        private static string RunGitCommand(string command, string workingDirectory = null)
        {
            var gitInfo = new ProcessStartInfo
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                FileName = "cmd.exe",
                Arguments = $@"/C ""git {command}""",
                UseShellExecute = false
            };

            var gitProcess = new Process();
            gitInfo.WorkingDirectory = workingDirectory;

            gitProcess.StartInfo = gitInfo;
            gitProcess.Start();

            var error = gitProcess.StandardError.ReadToEnd();  // pick up STDERR
            var output = gitProcess.StandardOutput.ReadToEnd(); // pick up STDOUT

            gitProcess.WaitForExit();
            gitProcess.Close();

            return output == "" ? null : output.Trim('\r', '\n', ' ');
        }

        public static void ShowInfoMessage(string message)
        {
            VsShellUtilities.ShowMessageBox(
                SuperBookmarksPackage.Instance,
                message,
                null,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        public static void ShowWarningMessage(string message)
        {
            VsShellUtilities.ShowMessageBox(
                SuperBookmarksPackage.Instance,
                message,
                null,
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        public static bool ShowYesNoQuestionMessage(string message)
        {
            const int YesButton = 6;

            return VsShellUtilities.ShowMessageBox(
                SuperBookmarksPackage.Instance,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == YesButton;
        }

        public static void ShowErrorMessage(string message, bool showHeader = true)
        {
            VsShellUtilities.ShowMessageBox(
                SuperBookmarksPackage.Instance,
                showHeader ?
                    "Something went wrong. The ugly details:\r\n\r\n" + message :
                    message,
                showHeader ? "SuperBookmarks" : null,
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            Debug("Error message: " + message);
        }

        public static bool IsTextDocument(ITextBuffer buffer) =>
            buffer.ContentType.IsOfType("text");

        public static ITextBuffer GetRootTextBuffer(ITextBuffer buffer)
        {
            if (buffer.ContentType.TypeName == "HTMLXProjection")
            {
                var projectionBuffer = buffer as IProjectionBuffer;
                return projectionBuffer == null
                    ? buffer
                    : projectionBuffer.SourceBuffers.FirstOrDefault(b => Helpers.IsTextDocument(b));
            }
            else
            {
                return Helpers.IsTextDocument(buffer) ? buffer : null;
            }
        }

        private static Dictionary<string, string> properlyCasedPaths = new Dictionary<string, string>();

        public static void ClearProperlyCasedPathsCache()
        {
            properlyCasedPaths.Clear();
        }

        public static string GetProperlyCasedPath(string path)
        {
            if(properlyCasedPaths.TryGetValue(path, out var properlyCasedPath))
                return properlyCasedPath;

            try
            {
                properlyCasedPath = GetProperlyCasedPathCore(path);
                if (properlyCasedPath == null)
                {
                    Helpers.LogError($"I couldn't get the properly cased version of '{path}' - bookmark navigation might not work properly");
                    properlyCasedPath = path;
                }
            }
            catch (Exception ex)
            {
                Helpers.LogError($"Error when trying to get the properly cased version of path '{path}':\r\n\r\n({ex.GetType().Name}){ex.Message}");
                properlyCasedPath = path;
            }
            
            properlyCasedPaths.Add(path, properlyCasedPath);
            return properlyCasedPath;
        }

        //https://stackoverflow.com/a/29578292/4574
        private static string GetProperlyCasedPathCore(string path)
        {
            // DirectoryInfo accepts either a file path or a directory path, and most of its properties work for either.
            // However, its Exists property only works for a directory path.
            DirectoryInfo directory = new DirectoryInfo(path);
            if (!File.Exists(path) && !directory.Exists)
                return null;

            List<string> parts = new List<string>();

            DirectoryInfo parentDirectory = directory.Parent;
            while (parentDirectory != null)
            {
                FileSystemInfo entry = parentDirectory.EnumerateFileSystemInfos(directory.Name).First();
                parts.Add(entry.Name);

                directory = parentDirectory;
                parentDirectory = directory.Parent;
            }

            // Handle the root part (i.e., drive letter or UNC \\server\share).
            string root = directory.FullName;
            if (root.Contains(':'))
            {
                root = root.ToUpper();
            }
            else
            {
                string[] rootParts = root.Split('\\');
                root = string.Join("\\", rootParts.Select(part => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(part)));
            }

            parts.Add(root);
            parts.Reverse();
            return Path.Combine(parts.ToArray());
        }

        public static string Quantifier(int count, string singularTerm)
            => $"{count} {singularTerm}{(count == 1 ? "" : "s")}";

        public static void WriteToStatusBar(string message)
        {
            StatusBar.IsFrozen(out int frozen);
            if (frozen != 0)
                StatusBar.FreezeOutput(0);

            StatusBar.SetText(message);

            StatusBar.FreezeOutput(1);

            Debug("StatusBar: " + message);
        }

        private static string activityLogFilePath = null;

        public static string ActivityLogFilePath =>
            activityLogFilePath ??
            (activityLogFilePath = GetActivityLogFilePath());

        private static string GetActivityLogFilePath() =>
            SafeInvoke(_GetActivityLogFilePath);

        private static string _GetActivityLogFilePath()
        {
            var shell = (IVsShell)((IServiceProvider)SuperBookmarksPackage.Instance).GetService(typeof(SVsShell));

            if (shell.GetProperty((int)__VSSPROPID.VSSPROPID_VirtualRegistryRoot, out object root) != VSConstants.S_OK)
                return null;

            var version = Path.GetFileName(root.ToString());
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appDataPath, "Microsoft\\VisualStudio", version, "ActivityLog.xml");
        }

        public static void LogError(string message)
        {
            try
            {
                if (SuperBookmarksPackage.Instance.DebugOptions.ShowErrorsInMessageBox)
                    WriteErrorToActivityLog(message);

                if (SuperBookmarksPackage.Instance.DebugOptions.ShowErrorsInMessageBox)
                    ShowErrorMessage("SuperBookmarks - Error:\r\n\r\n" + message, showHeader: false);

                if (SuperBookmarksPackage.Instance.DebugOptions.ShowErrorsInOutputWindow)
                    WriteToOutputWindow("*** SuperBookmarks: " + message + "\r\n");
            }
            catch
            {
                //¯\_(ツ)_/¯
            }
        }

        public static void LogException(Exception exception)
        {
            if (exception == null)
                return;

            string message;
            if (SuperBookmarksPackage.Instance.DebugOptions.WriteErrorsToActivityLog)
            {
                message = $"Unhandled exception: ({exception.GetType().Name}) {exception.Message}\r\n{exception.StackTrace}";
                WriteErrorToActivityLog(message);
            }

            if(SuperBookmarksPackage.Instance.DebugOptions.ShowErrorsInMessageBox)
            {
                message = $"SuperBookmarks - Unhandled exception:\r\n\r\n({exception.GetType().Name}) {exception.Message}";
                ShowErrorMessage(message, showHeader: false);
            }

            if (SuperBookmarksPackage.Instance.DebugOptions.ShowErrorsInOutputWindow)
            {
                message = $"*** SuperBookmarks - Unhandled exception:\r\n   ({exception.GetType().Name}) {exception.Message}\r\n{exception.StackTrace}\r\n";
                WriteToOutputWindow(message);
            }
        }

        private static void WriteErrorToActivityLog(string message)
        {
            var log = ((IServiceProvider)SuperBookmarksPackage.Instance).GetService(typeof(SVsActivityLog)) as IVsActivityLog;
            if (log == null) return;
            
            log.LogEntry((uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR, "SuperBookmarks", message);
        }

        private static void WriteToOutputWindow(string message)
        {
            var outWindow = SuperBookmarksPackage.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;

            var generalPaneGuid = VSConstants.GUID_OutWindowDebugPane;
            outWindow.GetPane(ref generalPaneGuid, out IVsOutputWindowPane pane);

            pane.OutputString(message);
            pane.Activate();
        }

        [Conditional("DEBUG")]
        public static void Debug(string message)
        {
            WriteToOutputWindow($"[SuperBookmarks] {DateTime.Now:H:mm:ss} {message} \r\n");
        }

        public static void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch(Exception ex)
            {
                try
                {
                    LogException(ex);
                }
                catch
                {
                    //¯\_(ツ)_/¯
                }
            }
        }

        public static T SafeInvoke<T>(Func<T> func, T defaultValue = default(T))
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                try
                {
                    LogException(ex);
                }
                catch
                {
                    //¯\_(ツ)_/¯
                }
                return defaultValue;
            }
        }
    }
}
