# 文件對稱加密課堂示範（C#）

本程式依簡報需求實作文件加密與解密，使用 .NET 8，不需要安裝額外的 NuGet 套件。這是課堂展示用的自製密碼，沒有經過公開密碼學分析，**不可用來保護真實機密資料**。

## 簡報要求對照

- **16 位元及 64 位元金鑰／區塊**：使用 `--block-size 16` 或 `--block-size 64`；此選項同時決定主金鑰與區塊大小。主金鑰由密碼衍生，再用 HMAC 展開為輪金鑰；每區塊分成左右兩半，以 8 輪 Feistel 結構處理。
- **至少五種運算**：輪函數包含 XOR 金鑰混合、4 位元 S-box 替換、位元排列、循環位移及模數加法。
- **五種操作模式**：ECB、CBC、CFB、OFB、CTR。ECB/CBC 使用 PKCS#7 填充；CFB/OFB/CTR 可直接處理任意長度資料。
- **密碼與完整性**：PBKDF2-HMAC-SHA256（200,000 次）衍生主金鑰，並使用 HMAC 衍生輪金鑰及獨立的檔案驗證金鑰。每次加密使用隨機 salt；CBC 另使用隨機 IV。解密前會驗證 HMAC，錯誤密碼或遭竄改的檔案會被拒絕。
- **舊版相容性**：C# 版沿用 Python 版的 `SEDF` 文件格式，能解密舊版 Python 產生的加密檔。

## 使用方式

### 互動使用（建議）

在此資料夾開啟 PowerShell，執行：

```powershell
dotnet run --project .\FileEncryptor.csproj
```

不帶參數啟動時，程式會詢問要加密或解密、原始文件路徑及輸出目錄路徑；輸出目錄必須已存在。文件路徑也可以直接從檔案總管拖到終端機視窗。程式會在指定的輸出目錄自動命名：加密檔使用原檔名加上 `.sed`（例如 `1.txt.sed`），解密 `.sed` 檔則移除 `.sed` 並加上 `.restored`（例如 `1.restored.txt`）。加密時還可選 ECB/CBC/CFB/OFB/CTR 與區塊大小，並輸入密碼。若自動產生的輸出檔已存在，程式會拒絕覆寫。

成功建置後，也可以直接雙擊 `bin\Debug\net8.0\FileEncryptor.exe`，進入相同互動選單。若執行檔不存在，先在資料夾中執行 `dotnet build`。

### 命令列使用

以下命令使用預設 CBC 模式及 64 位元區塊：

```powershell
dotnet run --project .\FileEncryptor.csproj -- encrypt ".\report.docx" ".\report.sed" --mode CBC --block-size 64
dotnet run --project .\FileEncryptor.csproj -- decrypt ".\report.sed" ".\report-restored.docx"
```

加密時需輸入並確認密碼；解密時輸入相同密碼。密碼輸入時不會顯示在畫面上，程式不會將密碼寫入命令列或檔案。解密時會自動讀取加密檔中的模式與區塊大小。

五種模式均可透過 `--mode ECB|CBC|CFB|OFB|CTR` 指定。以下為 CTR 範例：

```powershell
dotnet run --project .\FileEncryptor.csproj -- encrypt ".\report.docx" ".\report-ctr.sed" --mode CTR --block-size 64
```

輸出檔若已存在，程式會拒絕覆寫；請改用新的輸出檔名。請妥善保管密碼，遺失後無法解密。

## 模式比較

| 模式 | 特性 | 安全性比較 | 效能比較 |
|---|---|---|---|
| ECB | 每個區塊獨立加密，不需要隨機 IV | 相同明文區塊會產生相同密文區塊，容易暴露重複內容模式 | 區塊可獨立處理；此程式仍以單執行緒處理 |
| CBC | 每個明文區塊先與前一密文區塊 XOR；需要隨機 IV | 相同區塊在不同位置通常會產生不同密文；本程式使用 HMAC 偵測修改 | 需依序處理區塊，並多一次 XOR，通常略慢於 ECB |
| CFB | 將前一密文（首區塊為 IV）加密後，與明文 XOR；可處理部分區塊 | 不會像 ECB 直接暴露重複區塊；需要不可重複的 IV | 依賴前一區塊，需序列處理 |
| OFB | 反覆加密前一個輸出產生金鑰流，再與資料 XOR；可處理部分區塊 | IV 不可重複；錯誤會影響對應位元，但不會自行擴散 | 需序列產生金鑰流 |
| CTR | 將遞增計數器加密產生金鑰流，再與資料 XOR；可處理部分區塊 | 計數器區塊不可重複；可平行處理區塊 | 可平行處理，通常效率較好 |

所有模式使用相同的自製區塊密碼，模式選擇不會彌補區塊密碼本身的弱點。16 位元區塊及金鑰遠小於 64 位元，尤其 16 位元金鑰極易暴力猜測；兩種設定都不適合保護真實資料。16 位元 CTR 的計數器只有 65,536 種狀態，因此程式限制其單一文件最多 65,536 個區塊（128 KiB）；較大的檔案請選 64 位元區塊。

## 效能比較

在相同電腦執行下列指令，比較五種模式與兩種區塊大小。測量時間包含 PBKDF2 密碼衍生；16 位元 CTR 會以其最大容量 128 KiB 測試，其餘組合使用指定大小。結果會因電腦而異：

```powershell
dotnet run --project .\FileEncryptor.csproj -- benchmark --size-mib 1
```

## 自我測試

```powershell
dotnet run --project .\FileEncryptor.csproj -- self-test
```

測試涵蓋五種模式與兩種區塊大小、空檔案與任意長度資料、16 位元 CTR 容量限制、錯誤密碼、竄改偵測、舊版 Python 檔案相容性，以及避免覆寫既有輸出檔。
