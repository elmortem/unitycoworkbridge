using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class AgentBridgeQueueWindow : EditorWindow
    {
        private const string DragKey = "AgentBridge.QueuedTask";
        private List<PendingTaskInfo> _pending = new List<PendingTaskInfo>();
        private List<TaskRecord> _running = new List<TaskRecord>();
        private List<TaskRecord> _recent = new List<TaskRecord>();
        private TaskQueueSnapshot _nextSnapshot;
        private string _readError;
        private string _updated;
        private bool _showRecent = true;
        private List<Coordination.CoordinationRequest> _coordinationRequests = new List<Coordination.CoordinationRequest>();
        private Vector2 _scroll;
        private double _refreshAt;
        private string _message;

        [MenuItem("Tools/Agent Bridge/Task Queue")]
        public static void Open()
        {
            var window = GetWindow<AgentBridgeQueueWindow>("Agent Bridge Queue");
            window.minSize = new Vector2(480, 240);
            window.Show();
        }
        private void OnEnable() { EditorApplication.update -= Tick; EditorApplication.update += Tick; Refresh(); }
        private void OnDisable() { EditorApplication.update -= Tick; }
        private void Tick()
        {
            if (EditorApplication.timeSinceStartup < _refreshAt) return;
            Refresh();
            Repaint();
        }
        private void Refresh()
        {
            _refreshAt = EditorApplication.timeSinceStartup + 0.5;
            try
            {
                _nextSnapshot = TaskCoordinator.ReadQueueWindowSnapshot();
                _updated = System.DateTime.Now.ToString("HH:mm:ss");
            }
            catch (System.Exception e) { _readError = "Refresh failed: " + e.Message; }
        }
        private void OnGUI()
        {
            // IMGUI Layout and Repaint must render the same rows. update may run between them.
            if (Event.current.type == EventType.Layout && _nextSnapshot != null)
            {
                _pending = _nextSnapshot.Pending;
                _running = _nextSnapshot.Active;
                _recent = _nextSnapshot.Recent;
                _coordinationRequests = _nextSnapshot.CoordinationRequests;
                _readError = _nextSnapshot.Errors.Count == 0 ? null : "Incomplete snapshot: " + string.Join("; ", _nextSnapshot.Errors);
                _nextSnapshot = null;
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"Active: {_running.Count}   Queued: {_pending.Count}   Coordination: {_coordinationRequests.Count}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton)) { Refresh(); Repaint(); }
                if (GUILayout.Button("Automatic order", EditorStyles.toolbarButton))
                { TaskQueueOrder.Clear(); Refresh(); }
            }
            EditorGUILayout.HelpBox("Drag a queued task by its handle to change priority. Editor and coordination locks still apply. Running tasks stop cooperatively.", MessageType.Info);
            EditorGUILayout.SelectableLabel(BridgePaths.ProjectRoot, EditorStyles.miniLabel, GUILayout.Height(18));
            GUILayout.Label("Updated: " + (_updated ?? "waiting for editor"), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(_readError)) EditorGUILayout.HelpBox(_readError, MessageType.Warning);
            string reason = BridgeStatusWriter.Current.QueueBlockReason;
            if (!string.IsNullOrEmpty(reason)) EditorGUILayout.HelpBox(reason, MessageType.Warning);
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, MessageType.Warning);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var task in _running) Row(task.Id, task.Kind, task.Status, task.AgentSessionId, false);
            if (_pending.Count > 0) GUILayout.Label("Waiting — execution priority", EditorStyles.boldLabel);
            foreach (var task in _pending) Row(task.Id, task.Kind, "queued", task.EffectiveSessionId + " · " + task.Note,
                task.Kind != "cancel" && task.Kind != "stopplay");
            Rect end = GUILayoutUtility.GetRect(0, 35, GUILayout.ExpandWidth(true));
            Drop(end, null);
            if (_pending.Count == 0 && _running.Count == 0) GUILayout.Label("No submitted tasks in this project's queue.");
            if (_coordinationRequests.Count > 0)
            {
                GUILayout.Label("Coordination requests — ticket order", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("These agents are requesting permission before submitting tasks. Waiting requests keep their coordination order; granted requests hold permission and may not have submitted a task yet.", MessageType.None);
                foreach (var request in _coordinationRequests)
                {
                    using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.VerticalScope())
                        {
                            GUILayout.Label(request.Id + " · " + request.Session, EditorStyles.boldLabel);
                            GUILayout.Label(request.Kind + " · " + request.State + " · " + request.Reason);
                        }
                        using (new EditorGUI.DisabledScope(request.State != Coordination.CoordinationLimits.StateWaiting))
                            if (GUILayout.Button("Cancel", GUILayout.Width(65)))
                            { _message = TaskCoordinator.CancelCoordinationFromQueueWindow(request.Id); _refreshAt = 0; }
                    }
                }
            }
            _showRecent = EditorGUILayout.Foldout(_showRecent, "Recent results (last 10 minutes, up to 20)");
            if (_showRecent)
                foreach (var task in _recent)
                    Row(task.Id, task.Kind, task.Status,
                        task.Logs != null && task.Logs.Count > 0 ? task.Logs[task.Logs.Count - 1] : task.ReturnValue, false, false);
            EditorGUILayout.EndScrollView();
        }
        private void Row(string id, string kind, string status, string note, bool movable, bool canCancel = true)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Rect handle = GUILayoutUtility.GetRect(22, 42, GUILayout.Width(22));
                GUI.Label(handle, movable ? "≡" : "•");
                if (movable && Event.current.type == EventType.MouseDrag && handle.Contains(Event.current.mousePosition))
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.SetGenericData(DragKey, id);
                    DragAndDrop.StartDrag(id);
                    Event.current.Use();
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(new GUIContent(id, id), EditorStyles.boldLabel);
                    GUILayout.Label(kind + " · " + status + (string.IsNullOrEmpty(note) ? "" : " · " + note));
                }
                using (new EditorGUI.DisabledScope(status == "canceling" || !canCancel))
                    if (GUILayout.Button("Cancel", GUILayout.Width(65)))
                    {
                        _message = TaskCoordinator.CancelFromQueueWindow(id) ? null : "Task already finished or cannot be stopped in its current editor phase.";
                        _refreshAt = 0;
                    }
            }
            if (movable) Drop(GUILayoutUtility.GetLastRect(), id);
        }
        private void Drop(Rect rect, string before)
        {
            var evt = Event.current;
            string id = DragAndDrop.GetGenericData(DragKey) as string;
            if (id == null || !rect.Contains(evt.mousePosition)) return;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                TaskCoordinator.MoveQueuedTask(id, before);
                DragAndDrop.SetGenericData(DragKey, null);
                _refreshAt = 0;
            }
            evt.Use();
        }
    }
}
