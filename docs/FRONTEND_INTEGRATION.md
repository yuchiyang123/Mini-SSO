# 前端串接指南

Mini-SSO 是純後端 API（ASP.NET Core，無內建畫面），前端需要自己實作登入頁與呼叫下列 API。所有登入狀態都是透過 **HttpOnly Cookie** 保存的 JWT，前端**拿不到、也不需要拿到** token 本身。

## 基本原則

- 所有登入方式（帳密 / Google / GitHub）成功後，後端都會在回應中設定名為 `token` 的 `HttpOnly` Cookie。前端 JS 讀不到這個 Cookie（這是刻意設計，防 XSS 竊取 token），但瀏覽器會自動在後續請求帶上它。
- 因此前端呼叫本 API 時，**一定要帶上 `credentials: 'include'`**（fetch）或 `withCredentials: true`（axios），否則瀏覽器不會送出/接收 Cookie。
- 後端需要在 `Cors:AllowedOrigins`（或部署時的 `CORS_ALLOWED_ORIGINS` 環境變數）加入你的前端網域，否則瀏覽器會擋下跨網域請求。CORS 設定支援多個網域，用逗號分隔。
- 呼叫 `[Authorize]` 保護的端點（例如登出）時不需要手動加 `Authorization: Bearer ...` header，Cookie 會自動帶上。

## API 一覽

Base URL 以你部署的網址為準（本機開發預設 `http://localhost:5274`，Docker 部署預設 `http://localhost:8080`）。

### 帳密登入

```
POST /api/auth
Content-Type: application/json

{ "userName": "admin", "password": "admin123" }
```
- 成功：`200 OK`，並設定 `token` Cookie。
- 失敗（帳號不存在/密碼錯誤）：`400 Bad Request`。

```js
await fetch(`${API_BASE}/api/auth`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  credentials: 'include',
  body: JSON.stringify({ userName, password }),
});
```

### 註冊

```
POST /api/auth/create
Content-Type: application/json

{ "userName": "newuser", "password": "P@ssw0rd", "email": "user@example.com" }
```
- 成功：`200 OK`。
- 帳號重複：`400 Bad Request`，回傳 `ProblemDetails`，`errors.UserName` 會有錯誤訊息。

### 帳號是否已被使用

```
GET /api/auth/valid/username?username=someone
```
回傳 `true`/`false`（`true` = 可使用，`false` = 已被註冊）。適合拿來做即時輸入檢查。

### 登出

```
POST /api/auth/logout
```
需要已登入（帶有效 `token` Cookie），成功後會清掉 Cookie，回 `200 OK`；未登入呼叫會 `401 Unauthorized`。

```js
await fetch(`${API_BASE}/api/auth/logout`, { method: 'POST', credentials: 'include' });
```

### 第三方登入（Google / GitHub）

第三方登入**不是**用 fetch/AJAX 呼叫，而是**整頁導向**（跟一般網站的「用 Google 登入」按鈕一樣）：

```html
<a href="http://localhost:8080/api/auth/external/google/login">使用 Google 登入</a>
<a href="http://localhost:8080/api/auth/external/github/login">使用 GitHub 登入</a>
```

或用 JS 導頁：
```js
window.location.href = `${API_BASE}/api/auth/external/google/login`;
```

流程：
1. 使用者點擊連結 → 瀏覽器整頁導向後端 → 後端再導向 Google/GitHub 的授權頁。
2. 使用者在 Google/GitHub 頁面同意授權。
3. Google/GitHub 導回後端的 callback（`/signin-google`、`/signin-github`，這一步前端不用處理）。
4. 後端處理完成後，會設定 `token` Cookie，並把瀏覽器**導向 `Frontend:RedirectUrl` 設定的網址**（例如 `http://localhost:3000/sso/callback`）。
5. 前端在這個 `RedirectUrl` 頁面，此時使用者已經登入完成（Cookie 已經設定好），可以直接呼叫其他需要登入的 API，或導去首頁。

> `Frontend:RedirectUrl` 是後端環境變數 `FRONTEND_REDIRECT_URL` 設定的，前端**不需要**在網址上帶任何 code/token 參數自己解析，登入狀態已經在 Cookie 裡了。

若某個 provider 尚未設定金鑰，點擊該連結會得到 `503 Service Unavailable`（純文字訊息），前端可以用這個狀態碼判斷「此登入方式尚未開放」並隱藏/停用對應按鈕（可另外呼叫後端提供的健康檢查或在前端硬編碼已知可用清單）。

## 判斷目前是否已登入

目前 API 沒有提供「取得目前使用者資訊」的端點。前端若要判斷登入狀態，建議：
1. 呼叫任一個 `[Authorize]` 端點（例如先呼叫一次 `POST /api/auth/logout` 之外的保護端點），依 `401` 判斷未登入。
2. 或請後端後續補一個 `GET /api/auth/me` 回傳目前使用者資訊（目前尚未實作，如需要請再提出）。

## 常見錯誤排查

| 現象 | 可能原因 |
|---|---|
| fetch 一直失敗，Console 出現 CORS 錯誤 | 前端網域沒有加進後端的 `Cors:AllowedOrigins` |
| 登入 API 回 200 但下一支 API 卻 401 | fetch/axios 沒帶 `credentials: 'include'` / `withCredentials: true` |
| 點擊 Google/GitHub 登入後顯示 `redirect_uri_mismatch` | Google Cloud Console / GitHub OAuth App 裡設定的 callback URL 跟後端實際網址（`http(s)://<host>/signin-google` 或 `/signin-github`）不一致 |
| 點擊登入後端回 503 | 該 provider 尚未設定 `ClientId`/`ClientSecret` |
