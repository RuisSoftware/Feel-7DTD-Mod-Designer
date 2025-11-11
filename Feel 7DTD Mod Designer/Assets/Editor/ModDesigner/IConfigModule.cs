using UnityEngine;

public interface IConfigModule
{
    string ModuleName { get; }
    void Initialize(ModContext ctx);
    void OnGUIList(Rect rect);
    void OnGUIInspector(Rect rect);
    void Save();
}
