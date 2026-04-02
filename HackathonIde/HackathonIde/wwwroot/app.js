const NGROK_URL = "https://tova-seminivorous-lavona.ngrok-free.dev";
let editor, connection, timeout, isLoginMode = true;
let isApplyingRemoteChange = false;
let lastTypingTime = 0;

// --- 1. СИСТЕМА УВЕДОМЛЕНИЙ (WIN 11) ---
window.showNotification = function (message, type = "info") {
    const container = document.getElementById('notification-container');
    if (!container) return;
    const toast = document.createElement('div');
    toast.className = `win-notification ${type}`;
    const icons = { error: 'bi-exclamation-octagon-fill text-danger', success: 'bi-check-circle-fill text-success', info: 'bi-info-circle-fill text-info' };
    toast.innerHTML = `<i class="bi ${icons[type] || icons.info}"></i><div class="win-content"><div class="win-msg">${message}</div></div><button class="win-close" onclick="this.parentElement.remove()">&times;</button>`;
    container.appendChild(toast);
    setTimeout(() => { toast.classList.add('fade-out'); setTimeout(() => toast.remove(), 500); }, 5000);
}

window.customConfirm = function (message, onConfirm) {
    const modal = document.getElementById('win-confirm-modal');
    document.getElementById('win-confirm-msg').innerText = message;
    modal.style.display = 'flex';
    document.getElementById('btn-confirm-ok').onclick = () => { onConfirm(); modal.style.display = 'none'; };
}
window.closeConfirmModal = () => document.getElementById('win-confirm-modal').style.display = 'none';

// --- 2. API FETCH ---
async function apiFetch(url, options = {}) {
    const token = localStorage.getItem("jwt_token");
    const defaultHeaders = { "Content-Type": "application/json" };
    if (token) defaultHeaders["Authorization"] = `Bearer ${token}`;
    options.headers = { ...defaultHeaders, ...options.headers };
    const response = await fetch(url, options);
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
        showNotification(data.message || "Ошибка", "error");
        throw new Error(data.message);
    }
    return data;
}

// --- 3. УПРАВЛЕНИЕ ЭКРАНАМИ ---
window.switchScreen = function (screenId) {
    document.querySelectorAll('.screen').forEach(s => s.classList.remove('active'));
    document.getElementById(screenId).classList.add('active');
    const header = document.getElementById('global-header');
    if (header) {
        header.style.display = (screenId === 'screen-auth') ? 'none' : 'flex';
        const isIde = screenId === 'screen-ide';
        ['btn-header-delete', 'btn-header-leave', 'btn-header-save'].forEach(id => {
            const el = document.getElementById(id); if (el) el.style.display = isIde ? 'flex' : 'none';
        });
    }
}

// --- 4. SIGNALR (ПОД ТВОЙ EditorHub.cs) ---
function startSignalR(projectId) {
    const token = localStorage.getItem("jwt_token");
    if (connection) connection.stop();

    connection = new signalR.HubConnectionBuilder()
        .withUrl(`${NGROK_URL}/editorHub`, { accessTokenFactory: () => token, transport: signalR.HttpTransportType.WebSockets })
        .withAutomaticReconnect().build();

    // Прием кода (Full или Delta)
    connection.on("ReceiveCodeUpdate", (update) => {
        if (!editor) return;

        const isFull = typeof update === "string";
        if (!isFull && (Date.now() - lastTypingTime < 700)) return;

        isApplyingRemoteChange = true;
        try {
            if (update.range || update.Range) {
                const r = update.range || update.Range;
                const range = new monaco.Range(r.startLineNumber, r.startColumn, r.endLineNumber, r.endColumn);
                editor.getModel().applyEdits([{ range: range, text: update.text || update.Text, forceMoveMarkers: true }]);
            } else if (isFull && update !== editor.getValue()) {
                const pos = editor.getPosition();
                editor.setValue(update);
                if (pos) editor.setPosition(pos);
            }
        } finally {
            setTimeout(() => { isApplyingRemoteChange = false; }, 30);
        }
    });

    // Список участников (Task 6, 12)
    connection.on("UpdateUserList", (users) => {
        const list = document.getElementById('user-list');
        if (list) {
            list.innerHTML = users.map(u => `
                <div class="user-list-item">
                    <img src="${u.avatarUrl}" class="user-list-avatar">
                    <span class="small">${u.username}</span>
                </div>
            `).join('');
        }
    });

    connection.on("ReceiveTerminalOutput", (o) => printToTerminal(o));
    connection.on("ReceiveSystemEvent", (m) => {
        const l = document.getElementById('system-logs');
        if (l) { l.innerHTML += `<div>[${new Date().toLocaleTimeString()}] >> ${m}</div>`; l.scrollTop = l.scrollHeight; }
    });

    connection.start().then(() => connection.invoke("JoinProjectSession", projectId.toString()));
}

// --- 5. MONACO ---
function initMonaco(initialCode) {
    require.config({ paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs' } });
    require(['vs/editor/editor.main'], function () {
        editor = monaco.editor.create(document.getElementById('editor'), {
            value: initialCode, language: 'csharp', theme: 'vs-dark', automaticLayout: true, fontSize: 14, fontFamily: 'Fira Code'
        });

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => window.saveChanges());

        editor.onDidChangeModelContent((event) => {
            if (isApplyingRemoteChange || event.isFlush) return;
            lastTypingTime = Date.now();
            const pId = localStorage.getItem("current_project_id");

            // ГЛАВНЫЙ ФИКС: Шлем ровно 3 аргумента, как требует EditorHub.cs
            event.changes.forEach(change => {
                if (connection?.state === "Connected") {
                    connection.invoke("BroadcastCodeChange", pId.toString(), change, editor.getValue());
                }
            });
        });
        initResizers();
    });
}

// --- 6. ОСТАЛЬНЫЕ ФУНКЦИИ (WINDOW) ---
window.handleAuth = async function () {
    const u = document.getElementById("auth-username").value.trim();
    const p = document.getElementById("auth-password").value;
    const ep = isLoginMode ? "/api/auth/login" : "/api/auth/register";
    try {
        const d = await apiFetch(`${NGROK_URL}${ep}`, { method: "POST", body: JSON.stringify({ Username: u, Password: p }) });
        if (isLoginMode) {
            localStorage.setItem("jwt_token", d.token);
            localStorage.setItem("username", d.username);
            document.getElementById("user-display").innerText = d.username;
            window.switchScreen("screen-lobby");
        } else { showNotification("Успешно! Войдите.", "success"); window.toggleAuthMode(); }
    } catch (e) { }
}

window.toggleAuthMode = (e) => {
    if (e) e.preventDefault();
    isLoginMode = !isLoginMode;
    document.getElementById('btn-main-auth').innerText = isLoginMode ? "ВОЙТИ" : "СОЗДАТЬ";
}

window.handleCreateProject = async () => {
    const n = document.getElementById("create-name").value;
    const p = document.getElementById("create-pass").value;
    try {
        const d = await apiFetch(`${NGROK_URL}/api/projects/create`, { method: "POST", body: JSON.stringify({ Name: n, Password: p }) });
        await enterRoom(d.projectId, p);
    } catch (e) { }
}

window.handleJoinProject = async () => {
    const id = document.getElementById("join-id").value;
    const p = document.getElementById("join-pass").value;
    await enterRoom(id, p);
}

async function enterRoom(projectId, password) {
    try {
        const d = await apiFetch(`${NGROK_URL}/api/projects/${projectId}/join`, { method: "POST", body: JSON.stringify({ Password: password }) });
        localStorage.setItem("current_project_id", projectId);
        localStorage.setItem("current_project_pass", password);
        document.getElementById("active-project-id").innerText = projectId;
        window.switchScreen("screen-ide");
        if (!editor) initMonaco(d.currentCode || "");
        else { isApplyingRemoteChange = true; editor.setValue(d.currentCode || ""); isApplyingRemoteChange = false; }
        startSignalR(projectId);
    } catch (e) { localStorage.removeItem("current_project_id"); window.switchScreen("screen-lobby"); }
}

window.leaveRoom = async () => { if (connection) await connection.stop(); localStorage.removeItem("current_project_id"); window.switchScreen("screen-lobby"); }

window.deleteCurrentRoom = function () {
    const pId = localStorage.getItem("current_project_id");
    window.customConfirm(`Удалить проект №${pId} навсегда?`, async () => {
        try { await apiFetch(`${NGROK_URL}/api/projects/${pId}`, { method: 'DELETE' }); showNotification("Удалено", "success"); await leaveRoom(); } catch (e) { }
    });
}

window.saveChanges = async function () {
    const pId = localStorage.getItem("current_project_id");
    try { await apiFetch(`${NGROK_URL}/api/projects/${pId}/save`, { method: 'POST', body: JSON.stringify({ Code: editor.getValue() }) }); showNotification("Сохранено", "success"); } catch (err) { }
}

// Кнопки Undo/Redo (Task 8)
window.sendUndo = () => connection.invoke("RequestUndo", localStorage.getItem("current_project_id"));
window.sendRedo = () => connection.invoke("RequestRedo", localStorage.getItem("current_project_id"));

window.runCode = function () {
    const pId = localStorage.getItem("current_project_id");
    printToTerminal("> Выполнение...");
    apiFetch(`${NGROK_URL}/api/projects/${pId}/execute`, { method: 'POST', body: JSON.stringify({ Code: editor.getValue() }) })
        .then(d => {
            const out = d.terminalOutput || "Готово.";
            if (connection?.state === "Connected") connection.invoke("SendTerminalOutput", pId.toString(), out);
            else printToTerminal(out);
        });
}

window.analyzeCode = async () => {
    const aiBox = document.getElementById('ai-suggestions');
    aiBox.innerHTML = '<div class="small">Анализ...</div>';
    try {
        const d = await apiFetch(`${NGROK_URL}/api/projects/${localStorage.getItem("current_project_id")}/review`, { method: 'POST', body: JSON.stringify({ Code: editor.getValue() }) });
        aiBox.innerHTML = `<div class="ai-card" style="border-left: 3px solid var(--color-ai); padding:10px;">${d.suggestion}</div>`;
    } catch (e) { aiBox.innerHTML = "Ошибка AI."; }
}

function printToTerminal(msg) {
    const out = document.getElementById('terminal-output');
    if (out) { out.innerHTML += `<div>${msg}</div>`; out.scrollTop = out.scrollHeight; }
}

window.logout = () => { localStorage.clear(); location.reload(); };

function initResizers() {
    const resH = document.getElementById('resizer-h'); const side = document.getElementById('side-panel');
    const resV = document.getElementById('resizer-v'); const term = document.getElementById('terminal-panel');
    if (resH && side) {
        resH.onmousedown = (e) => {
            e.preventDefault(); document.body.classList.add('resizing-active');
            const doH = (m) => { let nw = window.innerWidth - m.clientX; nw = Math.max(250, Math.min(nw, window.innerWidth * 0.6)); side.style.width = nw + 'px'; side.style.flex = `0 0 ${nw}px`; if (editor) editor.layout(); };
            const stopH = () => { document.body.classList.remove('resizing-active'); document.removeEventListener('mousemove', doH); document.removeEventListener('mouseup', stopH); };
            document.addEventListener('mousemove', doH); document.addEventListener('mouseup', stopH);
        };
    }
    if (resV && term) {
        resV.onmousedown = (e) => {
            e.preventDefault(); document.body.classList.add('resizing-active');
            const doV = (m) => { let nh = window.innerHeight - m.clientY; nh = Math.max(40, Math.min(nh, window.innerHeight * 0.8)); term.style.height = nh + 'px'; term.style.flex = `0 0 ${nh}px`; if (editor) editor.layout(); };
            const stopV = () => { document.body.classList.remove('resizing-active'); document.removeEventListener('mousemove', doV); document.removeEventListener('mouseup', stopV); };
            document.addEventListener('mousemove', doV); document.addEventListener('mouseup', stopV);
        };
    }
}

document.addEventListener("DOMContentLoaded", () => {
    const token = localStorage.getItem("jwt_token");
    const user = localStorage.getItem("username");
    if (token && user) {
        document.getElementById("user-display").innerText = user;
        const lastRoomId = localStorage.getItem("current_project_id");
        const lastRoomPass = localStorage.getItem("current_project_pass");
        if (lastRoomId && lastRoomPass) enterRoom(lastRoomId, lastRoomPass);
        else window.switchScreen("screen-lobby");
    }
});