#!/usr/bin/env dotnet-script
// 全員・全種目・全ジャッジ満点テストデータ生成
// 出力: PR_PRINT_CHECK_SCORE_LIST_A4_20行満点テスト.json

using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;

var judges  = new[] { "A","B","C","D","E","F","G","H" };
var dances  = new[] { "S","C","R","P","J" };
// 背番号1〜20（通し）
var playerNos = Enumerable.Range(1, 20).Select(n => n.ToString()).ToArray();

// DV_Result 組み立て
var sogoArr = new JsonArray();
for (int i = 0; i < playerNos.Length; i++)
{
    sogoArr.Add(new JsonObject
    {
        ["総合順位番号"] = i + 1,
        ["背番号"]       = playerNos[i],
        ["総合得点"]     = (double)(judges.Length * dances.Length),  // 40
        ["総合順位表記"] = "1位"   // 全員同点
    });
}

var shomokuArr = new JsonArray();
for (int di = 0; di < dances.Length; di++)
{
    var senshuArr = new JsonArray();
    for (int pi = 0; pi < playerNos.Length; pi++)
    {
        var jdArr = new JsonArray();
        foreach (var j in judges)
        {
            jdArr.Add(new JsonObject
            {
                ["ジャッジ記号"]     = j,
                ["素点"]             = 1.0,
                ["順位点"]           = 0.0,
                ["ジャッジ無効FLAG"] = "0",
                ["SEND_FLAG"]        = "1",
                ["TES素点"]          = new JsonArray(),
                ["PCS素点"]          = new JsonArray(),
                ["GOE素点"]          = new JsonArray(),
                ["一般減点素点"]      = new JsonArray()
            });
        }
        senshuArr.Add(new JsonObject
        {
            ["背番号"]         = playerNos[pi],
            ["種目得点"]       = (double)judges.Length,   // 8
            ["種目順位番号"]   = pi + 1,
            ["種目順位表記"]   = "1位",
            ["ヒート番号"]     = (pi / 5) + 1,
            ["失格FLAG"]       = "0",
            ["棄権FLAG"]       = "0",
            ["SEND_FLAG"]      = "1",
            ["TES"]            = new JsonObject { ["TES得点"] = 0.0, ["TES詳細"] = new JsonArray() },
            ["PCS"]            = new JsonArray(),
            ["GOE"]            = new JsonArray(),
            ["一般減点"]       = new JsonArray(),
            ["順位法詳細"]     = new JsonObject(),
            ["ジャッジ詳細結果"] = jdArr
        });
    }
    shomokuArr.Add(new JsonObject
    {
        ["種目順"]   = di + 1,
        ["種目記号"] = dances[di],
        ["選手結果"] = senshuArr
    });
}

var dvResult = new JsonObject
{
    ["団体CD"]     = "JS",
    ["競技会NO"]   = "444444",
    ["区分番号"]   = "01",
    ["区分名"]     = "グランプリラテン",
    ["ラウンド番号"] = "010",
    ["ラウンド名"] = "１次予選",
    ["採点方式名"] = "チェック法",
    ["採点方式ID"] = "チェック法",
    ["総合結果"]   = sogoArr,
    ["種目結果"]   = shomokuArr
};

var pr = new JsonObject
{
    ["_comment"] = "PR_PRINT テストデータ — CHECK_SCORE_LIST_A4 全員全種目全ジャッジ満点（20組・8ジャッジ）",
    ["_note"]    = "data は DV_Result の生フォーマット。区分番号=01, ラウンド番号=010。20組すべて合計40点（全員1位）。",
    ["_note2"]   = "DA_Master_チェック法テスト.json / DS_Status_チェック法20組テスト.json と合わせて使用。",
    ["jobId"]    = "JS444444_CHECKSCORE_ALLONE_001",
    ["layoutId"] = "CHECK_SCORE_LIST_A4",
    ["copies"]   = 1,
    ["priority"] = 2,
    ["data"]     = dvResult
};

var opts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
var outPath = Path.Combine(AppContext.BaseDirectory, "PR_PRINT_CHECK_SCORE_LIST_A4_20行満点テスト.json");
File.WriteAllText(outPath, pr.ToJsonString(opts), new UTF8Encoding(false));
Console.WriteLine("生成: " + outPath);
Console.WriteLine($"背番号1〜20 / 種目S,C,R,P,J / ジャッジA〜H / 全素点=1 / 合計40点");
