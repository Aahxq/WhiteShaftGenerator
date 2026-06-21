using PromeRotation.Plugins;
using WhiteShaftGenerator.Recorder;

namespace WhiteShaftGenerator.Plugin;

[PromePlugin(
    id: "white-shaft-generator",
    name: "白轴生成器",
    author: "Ahxq",
    description: "记录 PR LogSystem 事件并导出 PR Timeline 文件。",
    version: "2.0.0")]
public sealed class WhiteShaftGeneratorPlugin : IPromePlugin
{
    private readonly WhiteShaftGeneratorService _recorder = new();

    public void Initialize()
    {
        _recorder.Initialize();
    }

    public void DrawConfigUI()
    {
        _recorder.Draw();
    }

    public void Dispose()
    {
        _recorder.Dispose();
    }
}

