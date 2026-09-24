using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;

namespace DLsiteHarness;

/// <summary>
/// 只用来「接住插件日志」的最小宿主实现。
/// <para>
/// 插件把「为什么失败」通过 <see cref="IPotatoVnApi.Log"/> 说给用户听（Cloudflare 拦截、
/// 名称搜索放弃、各分区都没搜到……）。没有这个东西，那些话在离线验证里全是隐形的 ——
/// 也就没法证明"失败时不是静默的"。
/// </para>
/// <para>
/// 用 <see cref="DispatchProxy"/> 生成实现，省掉 IPotatoVnApi 那二十多个成员的空实现。
/// 注意必须是 public 且非 sealed：运行时要生成一个继承它的代理类型。
/// </para>
/// </summary>
public class HostApiProxy : DispatchProxy
{
    public static readonly List<string> Logs = [];

    public static IPotatoVnApi Create()
    {
        Logs.Clear();
        return DispatchProxy.Create<IPotatoVnApi, HostApiProxy>();
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IPotatoVnApi.Log):
            {
                var msg = args?.ElementAtOrDefault(1)?.ToString() ?? string.Empty;
                Logs.Add(msg);
                Console.WriteLine($"    [插件日志] {msg}");
                return null;
            }
            case nameof(IPotatoVnApi.DeveloperEvent):
            case nameof(IPotatoVnApi.Info):
            case nameof(IPotatoVnApi.Event):
            {
                var msg = string.Join(" ", (args ?? []).Where(a => a is string));
                Logs.Add(msg);
                Console.WriteLine($"    [插件日志/{targetMethod.Name}] {msg}");
                return null;
            }
            case nameof(IPotatoVnApi.GetPluginPath):
                return AppContext.BaseDirectory;
        }

        // 其余宿主能力：返回该类型的默认值，别让插件因为桩不完整而炸
        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType is null || returnType == typeof(void)) return null;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(returnType.GetGenericArguments()[0])
                .Invoke(null, [null]);
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
