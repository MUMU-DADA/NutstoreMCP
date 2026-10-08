// net48 的 BCL 里没有 IsExternalInit，而 C# 9 的 record / init 访问器需要它。
// 编译器只要求这个类型存在（它是个标记），运行时不需要做任何事。
// 标准做法：自己声明一个，放在 namespace 外面避免污染业务代码。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
