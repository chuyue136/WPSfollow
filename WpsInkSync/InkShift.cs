using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace WpsInkSync;

/// <summary>
/// 墨迹平移器：把一次滚动量 (dx, dy, 单位 DIP) 应用到宿主画布上的全部墨迹。
/// 串行化，避免快速滚动时多个 TransformInkAsync 交叉执行。
/// </summary>
sealed class InkShift
{
    readonly ICanvasCompositionService _comp;
    readonly SemaphoreSlim _lock = new(1, 1);

    public InkShift(ICanvasCompositionService comp) => _comp = comp;

    /// <summary>累计位移，用于诊断与回归原位。</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public long Moves { get; private set; }

    public bool Available => _comp != null;

    public async Task<bool> ApplyAsync(double dx, double dy, CancellationToken ct = default)
    {
        if (_comp == null || (dx == 0 && dy == 0)) return false;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _comp.TransformInkAsync(new Matrix(1, 0, 0, 1, dx, dy), ct).ConfigureAwait(false);
            X += dx;
            Y += dy;
            Moves++;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>回到累计位移的原点：用于验证"平移出去再平移回来笔迹是否还在"。</summary>
    public Task<bool> ResetAsync(CancellationToken ct = default) => ApplyAsync(-X, -Y, ct);

    public string Status => Available ? $"Δ=({X:0},{Y:0}) n={Moves}" : "无合成服务";
}
