// 一次性補上舊用量紀錄的估算金額與匯率（0.4.88）。
//
// ⚠️ 這支腳本是**一次性的例外，不是新的慣例。**
//
// AiUsageLog 的設計是「金額與單價在寫入當下算好存起來，之後不再回填」——單價會變，
// 而這一頁最主要的用途是「本月 vs 上月」，歷史被新單價重算的話那個比較就失去意義。
// 自動路徑（AiUsageRecorder、EF migration）因此一律不回填，只有這支手動腳本會。
//
// 0.4.88 上線時有兩批舊資料需要人工處理：
//
//   1. 換了 deployment（例如 gpt-5.6-sol）卻沒在 appsettings 補單價，那段期間的
//      EstimatedCost 全是 null。補這一批是**誠實的**：單價一直是那個數字，只是當時沒設定。
//
//   2. 0.4.88 之前沒有 ExchangeRate 欄位，所有舊紀錄都沒有匯率。
//      ⚠️ 補這一批**不誠實**：填進去的是執行當天的匯率，不是那些呼叫發生當天的匯率。
//      這與本功能「當時匯率」的賣點相牴觸。腳本會在動手前把這件事再講一次。
//
// ⚠️ 只補「還是空的」欄位，已經有值的一律不動。
//
// ── 為什麼是 Node 而不是 PowerShell ─────────────────────────────────────────
// 這個 repo 的 scripts/ 其他檔案都是 .ps1，但這支不行：本機只有 Windows PowerShell 5.1，
// 它跑在 .NET Framework 上，載不了專案用的 .NET 10 版 Microsoft.Data.Sqlite
// （實測「無法載入一個或多個要求的類型」），而機器上沒有 pwsh 7。
// Node 24 內建 node:sqlite，不需要安裝任何東西，是這台機器上唯一跑得起來的選擇。
//
// ── 用法 ───────────────────────────────────────────────────────────────────
//   node scripts/Backfill-AiUsageCost.mjs --rate 31.863639 \
//        --model gpt-5.6-sol --input-per-million 4.0 --output-per-million 20.0
//
//   選項：
//     --db <path>              BackendDB.db 路徑（預設讀 appsettings.json）
//     --rate <number>          1 美金換多少目標幣別。省略則不補匯率
//     --target-currency <code> 預設 TWD
//     --model <name>           要補金額的模型名稱。省略則不補金額
//     --input-per-million      該模型每百萬輸入 token 的美金單價
//     --output-per-million     該模型每百萬輸出 token 的美金單價
//     --yes                    跳過確認
import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline/promises';
import { DatabaseSync } from 'node:sqlite';

function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (!arg.startsWith('--')) continue;
    const key = arg.slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) {
      options[key] = true;
    } else {
      options[key] = next;
      i++;
    }
  }
  return options;
}

const options = parseArgs(process.argv.slice(2));
const repoRoot = path.resolve(import.meta.dirname, '..');

// ── 找資料庫 ─────────────────────────────────────────────────────────────────
let databasePath = options.db;
if (!databasePath) {
  const appsettings = path.join(repoRoot, 'src/MeetingRecord/MeetingRecord.Web/appsettings.json');
  if (!fs.existsSync(appsettings)) {
    console.error('找不到 appsettings.json，請用 --db 指定資料庫位置。');
    process.exit(1);
  }
  const json = JSON.parse(fs.readFileSync(appsettings, 'utf8').replace(/^﻿/, ''));
  databasePath = path.join(json.SystemSettings.ExternalFileSystem.DatabasePath, 'BackendDB.db');
}

if (!fs.existsSync(databasePath)) {
  console.error(`找不到資料庫：${databasePath}`);
  process.exit(1);
}

const rate = options.rate ? Number(options.rate) : 0;
const targetCurrency = options['target-currency'] ?? 'TWD';
const model = typeof options.model === 'string' ? options.model : '';
const inputPerMillion = options['input-per-million'] ? Number(options['input-per-million']) : 0;
const outputPerMillion = options['output-per-million'] ? Number(options['output-per-million']) : 0;

const doRate = rate > 0;
const doCost = model !== '' && inputPerMillion > 0 && outputPerMillion > 0;

if (!doRate && !doCost) {
  console.error('沒有任何事情要做。請給 --rate，或給 --model 加上兩個單價。');
  process.exit(1);
}

console.log(`資料庫：${databasePath}`);

// ⚠️ 開之前先確認沒有別的行程佔著。App 是 WAL 模式，同時寫不會壞資料，
//    但使用者一邊操作一邊回填會讓「動了幾列」對不上。
const db = new DatabaseSync(databasePath);

try {
  console.log('\n將要異動的列數：');

  let costCount = 0;
  if (doCost) {
    costCount = db.prepare(`
      SELECT COUNT(*) AS c FROM AiUsageLog
      WHERE Model = ? AND EstimatedCost IS NULL
        AND InputTokens IS NOT NULL AND OutputTokens IS NOT NULL`).get(model).c;
    console.log(`  補金額（${model}，輸入 ${inputPerMillion}／輸出 ${outputPerMillion} 每百萬 token）：${costCount} 列`);
  }

  let rateCount = 0;
  if (doRate) {
    rateCount = db.prepare('SELECT COUNT(*) AS c FROM AiUsageLog WHERE ExchangeRate IS NULL').get().c;
    console.log(`  補匯率（1 USD = ${rate} ${targetCurrency}）：${rateCount} 列`);
    console.log('\n  ⚠️ 補進去的是「今天」的匯率，不是那些呼叫發生當天的匯率。');
    console.log('     本功能的賣點是「當時匯率」，這一批舊資料做不到，只能用今天的值近似。');
  }

  if (costCount === 0 && rateCount === 0) {
    console.log('\n沒有任何列需要補，結束。');
    process.exit(0);
  }

  if (!options.yes) {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    const answer = await rl.question('\n確認執行？輸入 yes 繼續：');
    rl.close();
    if (answer.trim() !== 'yes') {
      console.log('已取消，沒有異動任何資料。');
      process.exit(0);
    }
  }

  // ── 備份 ───────────────────────────────────────────────────────────────────
  // ⚠️ 只複製 .db 是不夠的。這個資料庫是 WAL 模式，App 開著的時候最近的交易還躺在
  //    -wal 檔裡沒有併回主檔——只備份 .db 會得到一份「看起來正常但少了最近幾筆」的檔案，
  //    而那正是真的需要還原時最糟的情況。先做 checkpoint，再把 sidecar 一起複製。
  const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
  const backup = `${databasePath}.bak-before-backfill-${stamp}`;

  try {
    db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
  } catch {
    // 別的行程正握著讀鎖時 checkpoint 會失敗。不是錯誤——下面連 -wal 一起複製就好。
    console.log('  （無法 checkpoint，改為連同 -wal／-shm 一起備份）');
  }

  fs.copyFileSync(databasePath, backup);
  for (const suffix of ['-wal', '-shm']) {
    if (fs.existsSync(databasePath + suffix)) {
      fs.copyFileSync(databasePath + suffix, backup + suffix);
    }
  }
  console.log(`已備份到：${backup}`);

  // ── 動手 ───────────────────────────────────────────────────────────────────
  // 一個交易包起來：補到一半失敗會讓「有金額但沒匯率」與「有匯率但沒金額」混在一起。
  db.exec('BEGIN');
  try {
    if (doCost) {
      // 與 AiUsagePricing.EstimateTokenCost 同一條算式：輸入與輸出分開計價
      // （輸出單價通常是輸入的 3～5 倍，合在一起算會錯得很離譜）。
      // ⚠️ 只補「有 token 用量」的列——失敗與取消的呼叫本來就沒有用量，不該被算出金額。
      db.prepare(`
        UPDATE AiUsageLog
        SET EstimatedCost = (InputTokens / 1000000.0) * ? + (OutputTokens / 1000000.0) * ?,
            InputPricePerMillion = ?,
            OutputPricePerMillion = ?
        WHERE Model = ? AND EstimatedCost IS NULL
          AND InputTokens IS NOT NULL AND OutputTokens IS NOT NULL`)
        .run(inputPerMillion, outputPerMillion, inputPerMillion, outputPerMillion, model);
    }

    if (doRate) {
      db.prepare(`
        UPDATE AiUsageLog
        SET ExchangeRate = ?, ConvertedCurrency = ?
        WHERE ExchangeRate IS NULL`).run(rate, targetCurrency);
    }

    db.exec('COMMIT');
  } catch (error) {
    db.exec('ROLLBACK');
    throw error;
  }

  // ── 交代結果 ───────────────────────────────────────────────────────────────
  console.log('\n完成。目前帳本狀態：');
  console.table(db.prepare(`
    SELECT Model AS 模型,
           COUNT(*) AS 筆數,
           SUM(CASE WHEN EstimatedCost IS NULL THEN 1 ELSE 0 END) AS 無金額,
           SUM(CASE WHEN ExchangeRate IS NULL THEN 1 ELSE 0 END) AS 無匯率
    FROM AiUsageLog GROUP BY Model`).all());
} finally {
  db.close();
}
