namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// Optional hook for the game's own components. If a "Waterslide"-tagged object has a
    /// MonoBehaviour implementing this, ChaosController calls Break() instead of falling back
    /// to the generic mesh-disable sabotage.
    /// </summary>
    public interface IBreakable
    {
        void Break();
    }
}
