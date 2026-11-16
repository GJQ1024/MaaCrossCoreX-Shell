using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

public class MaaToken
{
    private List<Dictionary<string, JToken>> Tokens = [];


    public void Merge(Dictionary<string, JToken> token)
    {
        Tokens.Add(CloneTokenDictionary(token));
    }

    /// <summary>
    /// 把多个 override 片段中同一节点的 action.param.custom_action_param 合并成一个字典：
    /// 后出现的 option 覆盖同名 key，其余 key 保留。
    /// 避免后一个 option 只写局部参数时，把前一个 option 写进去的参数整体冲掉
    /// （例如芯片筛选：品质 q_* / 主技能 t_* 被佩戴类型的 wear 覆盖成只剩 wear）。
    /// 其它字段仍保持原有「后出现整体替换」语义，不做改动。
    /// </summary>
    public void MergeCustomActionParams()
    {
        var occurrences = new Dictionary<string, List<(JObject Param, JObject CustomParam)>>();

        for (var i = 0; i < Tokens.Count; i++)
        {
            foreach (var (nodeName, nodeToken) in Tokens[i])
            {
                if (TryGetCustomActionParam(nodeToken, out var param, out var customParam))
                {
                    if (!occurrences.TryGetValue(nodeName, out var list))
                        occurrences[nodeName] = list = new List<(JObject, JObject)>();
                    list.Add((param, customParam));
                }
            }
        }

        foreach (var (_, list) in occurrences)
        {
            if (list.Count < 2)
                continue;

            var merged = new JObject();
            foreach (var (_, source) in list)
                DeepMergeObject(merged, source);

            // 每一处都写回合并结果：不删除任何节点/键，保持 override 结构不变，
            // 差异只落在 custom_action_param 内部。
            foreach (var (param, _) in list)
                param["custom_action_param"] = merged.DeepClone();
        }
    }

    private static bool TryGetCustomActionParam(JToken? nodeToken, out JObject param, out JObject customParam)
    {
        param = null!;
        customParam = null!;
        if (nodeToken is not JObject node)
            return false;
        if (node["action"] is not JObject action)
            return false;
        if (action["param"] is not JObject actionParam)
            return false;
        if (actionParam["custom_action_param"] is not JObject custom)
            return false;

        param = actionParam;
        customParam = custom;
        return true;
    }

    private static void DeepMergeObject(JObject target, JObject source)
    {
        foreach (var property in source.Properties().ToList())
        {
            if (target[property.Name] is JObject targetChild && property.Value is JObject sourceChild)
                DeepMergeObject(targetChild, sourceChild);
            else
                target[property.Name] = property.Value.DeepClone();
        }
    }

    public static MaaToken FromDictionary(Dictionary<string, JToken> token)
    {
        MaaToken result = new MaaToken();
        result.Merge(token);
        return result;
    }

    public override string ToString()
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };
        return JsonConvert.SerializeObject(Tokens, settings);
    }

    private static Dictionary<string, JToken> CloneTokenDictionary(Dictionary<string, JToken> token)
    {
        return token.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.DeepClone());
    }
}
