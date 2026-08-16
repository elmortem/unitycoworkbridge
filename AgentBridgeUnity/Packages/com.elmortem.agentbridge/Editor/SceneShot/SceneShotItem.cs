using UnityEngine;

namespace AgentBridge.SceneShot
{
	public class SceneShotItem
	{
		public string Name;
		public string View = "scene";
		public int Width;
		public int Height;
		public bool Gizmos;
		public bool Grid;
		public SceneShotPoseMode Mode;
		public SceneShotPose Pose;
		public string FrameTarget;
		public float FrameMargin;
		public Vector3 FrameRotation;
		public bool Orthographic;
	}
}
