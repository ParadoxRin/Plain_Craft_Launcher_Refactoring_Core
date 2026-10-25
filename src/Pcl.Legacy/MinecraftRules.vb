' Derived from PCL 2.99 ModMinecraft.McJsonRuleCheck (see PCL-LICENCE.txt).
' Mac port: explicit target context replaces Windows-only globals and UI state.
Imports Newtonsoft.Json.Linq
Imports System.Text.RegularExpressions

' 功能：复用 PCL 的 Mojang rules 判断；平台及功能开关作为参数传入，不依赖 Windows 全局状态。
Public NotInheritable Class MinecraftRules
    ' 按清单顺序评估规则：匹配 OS、架构、系统版本和 feature 后，最后一条匹配规则决定 allow。
    Public Shared Function Check(rules As JToken, osName As String, architecture As String,
                                 osVersion As String, features As IReadOnlyDictionary(Of String, Boolean)) As Boolean
        If rules Is Nothing Then
            Return True
        End If
        Dim required As Boolean = False
        ' 规则默认拒绝；只在整条条件成立时更新结果，空规则集合仍保持拒绝。
        For Each rule As JToken In rules
            Dim matches As Boolean = True
            Dim os As JToken = rule("os")
            ' OS 条件逐项求交；正则设为 1 秒超时，避免异常元数据阻塞启动器。
            If os IsNot Nothing Then
                If os("name") IsNot Nothing Then
                    matches = matches AndAlso CStr(
                        os("name")
                    ) = osName
                End If
                If os("arch") IsNot Nothing Then
                    matches = matches AndAlso Regex.IsMatch(
                        architecture,
                        CStr(
                            os("arch")
                        ),
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1)
                    )
                End If
                If os("version") IsNot Nothing Then
                    matches = matches AndAlso Regex.IsMatch(
                        osVersion,
                        CStr(
                            os("version")
                        ),
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1)
                    )
                End If
            End If
            ' 未提供的 feature 按 False 处理；逐项比较布尔值，再与已有平台匹配结果合并。
            If TypeOf rule("features") Is JObject Then
                For Each feature As JProperty In DirectCast(
                    rule("features"),
                    JObject
                ).Properties()
                    Dim actual As Boolean = False
                    features.TryGetValue(feature.Name, actual)
                    matches = matches AndAlso actual = CBool(feature.Value)
                Next
            End If
            If matches Then
                required = CStr(
                    rule("action")
                ) = "allow"
            End If
        Next
        Return required
    End Function
End Class
