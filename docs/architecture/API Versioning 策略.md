# API Versioning 策略

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.4.118
- 首次實作版本：0.1.61
- 最後核對日期：2026/10/08

## 目標
- [x] 先建立版本化策略文件，但不在本輪改變既有 `/api/...` 路由，避免破壞目前 API 用戶端。

## 現況
- [x] Controller 目前同時提供 `[Route("api/[controller]")]` 與 `[Route("api/v1/[controller]")]`。
- [x] integration tests 已驗證既有 `/api/...` contract 與 `/api/v1/Auth/login`。
- [x] Swagger UI 已設定 v1 文件入口；本階段未導入額外 API Versioning 套件，先以平行路由維持相容。

## 後續導入策略
- [x] 第一階段先保留 `/api/...`，新增 `/api/v1/...` 平行路由，不立即移除舊路由。
- [x] Swagger 分組顯示 `v1`，讓新用戶端從版本化路由開始串接。
- [ ] 文件標示 `/api/...` 為相容路由，設定 sunset 日期後再移除。
- [ ] 所有 versioned endpoint 仍固定回傳 `ApiResult<T>`，並維持 DTO 邊界。
- [x] integration tests 同時覆蓋相容路由與版本化路由，避免遷移期間 regression。

## 驗收標準
- [x] `/api/...` 舊路由在公告期內不破壞。
- [x] `/api/v1/...` 與 Swagger 分組可正常運作。
- [x] CI 測試覆蓋兩套路由的主要 API contract。

## 備註風險
- [x] 本輪不實作版本化路由是刻意決策；直接切換會破壞現有 API 用戶端。
