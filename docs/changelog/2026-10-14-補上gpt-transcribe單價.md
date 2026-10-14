# 補上 gpt-transcribe 轉錄單價（0.4.123 → 0.4.124）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.124
- 首次實作版本：0.4.124
- 最後核對日期：2026/10/14

## 問題

系統健康監控的「AI 供應商設定」亮黃燈：「以下模型沒有設定單價（LlmSettings:Pricing），AI 用量分析的金額會顯示『—』：gpt-transcribe」。

原因是設定缺漏，不是程式錯誤：

- 本機 user-secrets 把轉錄模型設為 `gpt-transcribe`，這是 Azure 的部署名稱。
- `LlmSettings:Pricing` 以部署名稱為鍵，只有 `gpt-4o-transcribe`、`gpt-4o-mini-transcribe`，沒有 `gpt-transcribe`。
- 因此健康監控判黃燈，轉錄用量的金額也記成空白。

## 修正

- `appsettings.json` 的 `LlmSettings:Pricing` 加上 `gpt-transcribe`：每分鐘 **0.0045 美元**。
  - 價格依 OpenAI 官方頁面（https://developers.openai.com/api/docs/models/gpt-transcribe）。
  - Azure 帳單若不同，改這個數字即可。
  - user-secrets 的 `Pricing` 沒有這個鍵，組態合併後 appsettings.json 的值會生效，不必改 secrets。
- 單價只對之後的新呼叫生效（帳本「寫入當下算好存起來」的設計）。本機帳本沒有 `gpt-transcribe` 的舊紀錄，所以**不做回填**。其他環境若有舊紀錄要補，見 `scripts/Backfill-AiUsageCost.mjs` 的說明。
- 新增測試 `SystemHealthChecksTests.AiProvider_ShippedPricing_ShouldCoverTheDeploymentsInUse`：讀出貨的 `appsettings.json`，確認 `gpt-5.6-sol`＋`gpt-transcribe`、`gpt-4o-mini`＋`gpt-4o-transcribe` 兩組都不會因單價亮黃燈。

## 順帶處理（不在 repo 裡）

- 本機 user-secrets 殘留 `SystemSettings:SystemInformation:SystemVersion = 0.4.91 (2026/09/22)`，會蓋掉 appsettings.json 的版本號，畫面上的版本一直是 0.4.91。
- 已刪除這一個鍵，其他設定都沒動；原檔備份為同目錄的 `secrets.json.bak-<時間>`。
- `日誌與設定檔說明.md` 補上提醒：`SystemVersion` 不要放進 user-secrets 或環境變數。

## 異動檔案

| 檔案 | 異動 |
|---|---|
| `Web/appsettings.json` | `Pricing` 加 `gpt-transcribe`；版本 0.4.124 |
| `Tests/SystemHealthChecksTests.cs` | 新增出貨單價表的測試 |
| `docs/prd/AI用量分析-prd.md`、`docs/operations/日誌與設定檔說明.md` | 單價清單、版本沿革、SystemVersion 提醒 |
