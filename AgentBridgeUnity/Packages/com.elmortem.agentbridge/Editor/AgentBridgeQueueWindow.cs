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
        private void OnEnable() { EditorApplication.update += Tick; Refresh(); }
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
            _pending = TaskCoordinator.GetQueueSnapshot();
            _running = TaskCoordinator.GetRunningSnapshot();
        }
        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"Active: {_running.Count}   Queued: {_pending.Count}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Automatic order", EditorStyles.toolbarButton))
                { TaskQueueOrder.Clear(); Refresh(); }
            }
            EditorGUILayout.HelpBox("Drag a queued task by its handle to change priority. Editor and coordination locks still apply. Running tasks stop cooperatively.", MessageType.Info);
            string reason = BridgeStatusWriter.Current.QueueBlockReason;
            if (!string.IsNullOrEmpty(reason)) EditorGUILayout.HelpBox(reason, MessageType.Warning);
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, MessageType.Warning);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var task in _running) Row(task.Id, task.Kind, task.Status, task.AgentSessionId, false);
            if (_pending.Count > 0) GUILayout.Label("Waiting — execution priority", EditorStyles.boldLabel);
            foreach (var task in _pending) Row(task.Id, task.Kind, "queued", task.Note, true);
            Rect end = GUILayoutUtility.GetRect(0, 35, GUILayout.ExpandWidth(true));
            Drop(end, null);
            if (_pending.Count == 0 && _running.Count == 0) GUILayout.Label("No tasks in the queue.");
            EditorGUILayout.EndScrollView();
        }
        private void Row(string id, string kind, string status, string note, bool movable)
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
                using (new EditorGUI.DisabledScope(status == "canceling"))
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
