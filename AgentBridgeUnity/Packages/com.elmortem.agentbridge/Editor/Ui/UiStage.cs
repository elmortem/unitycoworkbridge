using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AgentBridge.Ui
{
	// Holds the temporary scene, camera, canvas and prefab instance used to
	// measure or render a UI prefab, plus the render-pipeline state saved while
	// the stage is open.
	public class UiStage
	{
		public Scene Scene;
		public Camera Camera;
		public Canvas Canvas;
		public GameObject Instance;
		public RenderTexture Texture;
		public int Width;
		public int Height;
		public bool Render;

		public bool PipelineOverridden;
		public RenderPipelineAsset SavedQualityPipeline;
		public RenderPipelineAsset SavedGraphicsPipeline;
	}
}
