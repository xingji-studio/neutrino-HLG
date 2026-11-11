using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "hlg-model-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var modelPath = Path.Combine(root, "model.json");
    File.WriteAllText(modelPath, """
      {"providers":[
        {"name":"A","models":[
          {"name":"same","api_key":"secret-a","url":"https://private.invalid/v1","intensity":[" MAX ","low","high","max","unknown"]},
          {"name":"all","api_key":"","intensity":["unknown"]}
        ]},
        {"name":"B","models":[{"name":"same","api_key":"secret-b","intensity":"medium"}]}
      ]}
    """);
    File.WriteAllText(Path.Combine(root, "config.json"), """
      {"selected_provider":"B","selected_model":"same","selected_intensity":"MEDIUM","default_intensity":"Low"}
    """);
    var result = NeutrinoModels.Read(root);
    if (result.Providers.Length != 2 || result.Providers[0].Models.Length != 2 || result.Providers[1].Models[0].Name != "same") throw new Exception("未保留提供商和模型列表。");
    if (result.Selection != new NeutrinoModels.Selection("B", "same", "Medium")) throw new Exception("默认模型或强度与 Neutrino 配置不一致。");
    if (!result.Providers[0].Models[0].Intensities.SequenceEqual(new[] { "Low", "High", "Max" })) throw new Exception("强度排序或大小写不一致。");
    if (!result.Providers[0].Models[1].Intensities.SequenceEqual(new[] { "Low", "Medium", "High", "Max", "Ultra" })) throw new Exception("未知强度的回退不一致。");
    var response = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (response.Contains("secret-a") || response.Contains("secret-b") || response.Contains("private.invalid") || response.Contains("api_key")) throw new Exception("模型接口泄露了密钥或接口地址。");
    File.WriteAllText(Path.Combine(root, "config.json"), """
      {"selected_provider":"A","selected_model":"same","selected_intensity":"Ultra","default_intensity":"max"}
    """);
    if (NeutrinoModels.Read(root).Selection.Intensity != "Max") throw new Exception("不支持的强度没有回退到配置的强度。");
    File.WriteAllText(modelPath, """{"providers":[{"name":"New","models":[{"name":"new-model","api_key":"x"}]}]}""");
    if (NeutrinoModels.Read(root).Providers.Single().Models.Single().Name != "new-model") throw new Exception("模型文件修改后没有重新读取。");
    File.WriteAllText(modelPath, "[]");
    try { NeutrinoModels.Read(root); throw new Exception("无效的模型文件未被拒绝。"); } catch (InvalidDataException) { }
    File.Delete(modelPath);
    try { NeutrinoModels.Read(root); throw new Exception("模型文件缺失时没有报错。"); } catch (FileNotFoundException) { }
    Console.WriteLine("模型目录、默认选择、强度兼容、实时刷新和密钥脱敏测试通过。");
} finally { Directory.Delete(root, true); }
