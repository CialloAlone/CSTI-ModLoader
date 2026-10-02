// MapperObject.cs 里有一句 `using CSTI_MiniLoader.Patchers;`（原工程遗留，未被使用）。
// 冒烟测试只链接纯托管文件，这里补一个空命名空间让该 using 成立。
namespace CSTI_MiniLoader.Patchers
{
    internal static class NamespaceAnchor
    {
    }
}
