# 客户端符号与版本基线

`ClientSymbols.cs` 是客户端混淆类型、方法和已使用数值常量的唯一源码登记。调用实现、调用点说明、两层危险方法清单、探针与测试均引用语义名称，以源码 link 编译，不增加默认域依赖。

当前基线是客户端 **7.2.12.0**：`baseline-7.2.12.json`。它包含 55 项登记、2 个程序集，涵盖已开放入口、禁止入口、事件调查入口、路径长度、权限/对象/错误/同步状态枚举，以及路径构造、文件 Builder、DAO 和写入响应契约。基线记录所有同名重载的完整签名、可见性、static/ref/out、默认参数、IL 与方法体结构 SHA256、枚举值，以及程序集版本、MVID 和 SHA256。

日常检查：

```powershell
.\deploy\check-client-bindings.ps1
.\deploy\check-client-bindings.ps1 -ClientDir 'C:\Program Files\Nutstore\bin-新版' -ReportPath .test-tmp\client-bindings.json
```

检查器只用 PEReader 读取文件，不加载或执行客户端代码；默认不写文件，显式报告仅写指定路径。任何成员、签名、数值、方法体或程序集差异均返回失败。`deploy/build.ps1`、安装和重启脚本会在改部署文件或停止客户端前进行检查；卸载不受基线限制。`verify.ps1` 检查混淆字面量未散落并运行检查器的离线自测。

升级时先保留旧基线并运行只读差异报告。逐项重新确认方法实现、调用方向、副作用和保护条件，更新登记后以新的文件名记录基线。签名相同不能证明语义相同，文件指纹相同也不能证明服务器行为或未登记依赖不变。禁止仅刷新基线消除差异。

确实完成复核后，显式新建基线：

```powershell
dotnet run --project recon/ClientBindingCheck -- --record --client-dir '<新版目录>' --baseline '<新基线文件>'
```

`--record` 拒绝覆盖已有文件，缺成员、常量值错误或登记版本不符也会拒绝。更新 `VerifiedClientVersion` 与 `BaselineFileName` 后，一起提交登记、基线和验证证据。删除入口虽在基线中，它们仍受两层禁止清单保护，检查不会执行它们。
