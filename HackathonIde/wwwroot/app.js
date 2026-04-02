const NGROK_URL = "https://tova-seminivorous-lavona.ngrok-free.dev";
let editor;
let connection;
let timeout;
let isLoginMode = true;
let isApplyingRemoteChange = false;
let lastTypingTime = 0;
let pendingAiCode = "";

// --- УТИЛИТА UX: ОБРАБОТКА ОШИБОК ---
async function apiFetch(url, options = {}) {
    const token = localStorage.getItem("jwt_token");
    const defaultHeaders = { "Content-Type": "application/json" };
    if (token) defaultHeaders["Authorization"] = `Bearer ${token}`;

    options.headers = { ...defaultHeaders, ...options.headers };

    const response = await fetch(url, options);
    const data = await response.json().catch(() => ({}));

    if (!response.ok) {
        const errorMsg = data.message || `Ошибка: ${response.status}`;
        if (response.status === 403) alert("Ошибка: Это может сделать только создатель комнаты!");
        else if (response.status === 409) alert(errorMsg);
        else alert(errorMsg);
        throw new Error(errorMsg);
    }
    return data;
}

// --- 1. УПРАВЛЕНИЕ ЭКРАНАМИ ---
window.switchScreen = function (screenId) {
    const screens = document.querySelectorAll('.screen');
    screens.forEach(s => s.classList.remove('active'));
    document.getElementById(screenId).classList.add('active');

    const header = document.getElementById('global-header');
    if (header) header.style.display = (screenId === 'screen-auth') ? 'none' : 'flex';
}

document.addEventListener("DOMContentLoaded", () => {
    const token = localStorage.getItem("jwt_token");
    const user = localStorage.getItem("username");
    if (token && user) {
        document.getElementById("user-display").innerText = user;
        window.switchScreen("screen-lobby");
    }
});

// --- 2. АВТОРИЗАЦИЯ (ФИКС: ЯВНОЕ ПРИСВОЕНИЕ WINDOW) ---
window.handleAuth = async function () {
    const username = document.getElementById("auth-username").value.trim();
    const password = document.getElementById("auth-password").value;
    if (!username || !password) return alert("Заполните поля");

    try {
        const endpoint = isLoginMode ? "/api/auth/login" : "/api/auth/register";
        const data = await apiFetch(`${NGROK_URL}${endpoint}`, {
            method: "POST",
            body: JSON.stringify({ Username: username, Password: password })
        });

        if (isLoginMode) {
            localStorage.setItem("jwt_token", data.token);
            localStorage.setItem("username", data.username);
            document.getElementById("user-display").innerText = data.username;
            window.switchScreen("screen-lobby");
        } else {
            alert("Регистрация успешна! Войдите.");
            window.toggleAuthMode();
        }
    } catch (e) { }
}

window.toggleAuthMode = function (e) {
    if (e) e.preventDefault();
    isLoginMode = !isLoginMode;
    const btn = document.getElementById('btn-main-auth');
    const link = document.querySelector("a[onclick*='toggleAuthMode']");
    const title = document.querySelector("#screen-auth h1");
    if (isLoginMode) {
        title.innerText = "OnliSharpTeam";
        btn.innerHTML = '<i class="bi bi-box-arrow-in-right"></i> ВОЙТИ';
        link.innerText = "Нет аккаунта? Регистрация";
    } else {
        title.innerText = "Регистрация";
        btn.innerHTML = '<i class="bi bi-person-plus"></i> СОЗДАТЬ АККАУНТ';
        link.innerText = "Уже есть аккаунт? Войти";
    }
}

// --- 3. ЛОББИ И КОМНАТЫ (ФИКС: ЯВНОЕ ПРИСВОЕНИЕ WINDOW) ---
window.handleCreateProject = async function () {
    const name = document.getElementById("create-name").value;
    const password = document.getElementById("create-pass").value;
    if (!name) return alert("Введите название проекта");

    try {
        const data = await apiFetch(`${NGROK_URL}/api/projects/create`, {
            method: "POST",
            body: JSON.stringify({ Name: name, Password: password })
        });
        await enterRoom(data.projectId, password);
    } catch (e) { }
}

window.handleJoinProject = async function () {
    const id = document.getElementById("join-id").value;
    const password = document.getElementById("join-pass").value;
    if (!id) return alert("Введите ID");
    await enterRoom(id, password);
}

async function enterRoom(projectId, password) {
    try {
        const data = await apiFetch(`${NGROK_URL}/api/projects/${projectId}/join`, {
            method: "POST",
            body: JSON.stringify({ Password: password })
        });
        localStorage.setItem("current_project_id", projectId);
        document.getElementById("active-project-id").innerText = projectId;
        window.switchScreen("screen-ide");

        if (!editor) initMonaco(data.currentCode || "// Начинаем кодить...\n");
        else {
            isApplyingRemoteChange = true;
            editor.setValue(data.currentCode || "");
            isApplyingRemoteChange = false;
        }
        startSignalR(projectId);
    } catch (e) { }
}

window.leaveRoom = async function () {
    if (connection) await connection.stop();
    localStorage.removeItem("current_project_id");
    window.switchScreen("screen-lobby");
}

window.deleteCurrentRoom = async function () {
    const pId = localStorage.getItem("current_project_id");
    if (!confirm("Вы уверены, что хотите УДАЛИТЬ эту комнату?")) return;
    try {
        await apiFetch(`${NGROK_URL}/api/projects/${pId}`, { method: 'DELETE' });
        alert("Проект удален");
        await leaveRoom();
    } catch (e) { }
}

// --- 4. SIGNALR ---
function startSignalR(projectId) {
    const token = localStorage.getItem("jwt_token");
    if (connection) connection.stop();

    connection = new signalR.HubConnectionBuilder()
        .withUrl(`${NGROK_URL}/editorHub`, {
            accessTokenFactory: () => token,
            transport: signalR.HttpTransportType.WebSockets
        })
        .withAutomaticReconnect().build();

    connection.on("ReceiveCodeUpdate", (update) => {
        if (!editor || Date.now() - lastTypingTime < 1200) return;
        isApplyingRemoteChange = true;
        try {
            if (update.range) {
                const range = new monaco.Range(
                    update.range.startLineNumber, update.range.startColumn,
                    update.range.endLineNumber, update.range.endColumn
                );
                editor.getModel().applyEdits([{ range: range, text: update.text, forceMoveMarkers: true }]);
            } else if (typeof update === "string") {
                editor.setValue(update);
            }
        } finally {
            setTimeout(() => { isApplyingRemoteChange = false; }, 50);
        }
    });

    connection.on("UpdateUserList", (users) => {
        const list = document.getElementById('user-list');
        if (list) {
            list.innerHTML = users.map(u => `
                <div class="user-list-item">
                    <img src="${u.avatarUrl}" class="user-list-avatar">
                    <span class="small text-light">${u.username}</span>
                </div>
            `).join('');
        }
    });

    connection.on("ReceiveAiResolution", (resolvedCode) => {
        pendingAiCode = resolvedCode;
        document.getElementById('ai-resolved-code').innerText = resolvedCode;
        document.getElementById('ai-merge-modal').style.display = 'flex';
    });

    connection.on("ReceiveTerminalOutput", (output) => printToTerminal(output));

    connection.on("ReceiveSystemEvent", (message) => {
        const logs = document.getElementById('system-logs');
        if (logs) {
            logs.innerHTML += `<div class="mb-1" style="color:var(--color-ai)">[${new Date().toLocaleTimeString()}] >> ${message}</div>`;
            logs.scrollTop = logs.scrollHeight;
        }
    });

    connection.start().then(() => connection.invoke("JoinProjectSession", projectId.toString()));
}

// КНОПКИ ДЕЙСТВИЙ (WINDOW)
window.sendUndo = () => connection.invoke("RequestUndo", localStorage.getItem("current_project_id"));
window.sendRedo = () => connection.invoke("RequestRedo", localStorage.getItem("current_project_id"));
window.simulateConflict = () => {
    const pId = localStorage.getItem("current_project_id");
    const codeA = editor.getValue();
    const codeB = codeA + "\n// Conflict Simulation";
    connection.invoke("TriggerAiConflictResolution", pId, codeA, codeB);
};
window.applyAiResolution = () => {
    isApplyingRemoteChange = true;
    editor.setValue(pendingAiCode);
    isApplyingRemoteChange = false;
    window.closeAiModal();
};
window.closeAiModal = () => document.getElementById('ai-merge-modal').style.display = 'none';

// --- 5. MONACO ---
function initMonaco(initialCode) {
    require.config({ paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs' } });
    require(['vs/editor/editor.main'], function () {
        editor = monaco.editor.create(document.getElementById('editor'), {
            value: initialCode, language: 'csharp', theme: 'vs-dark', automaticLayout: true, fontSize: 14
        });

        editor.onDidChangeModelContent((event) => {
            if (isApplyingRemoteChange || event.isFlush) return;
            lastTypingTime = Date.now();
            event.changes.forEach(change => {
                if (connection?.state === "Connected") {
                    connection.invoke("BroadcastCodeChange", localStorage.getItem("current_project_id"), {
                        range: change.range, text: change.text
                    });
                }
            });
        });
        initResizers();
    });
}

// --- ФУНКЦИИ ВЫПОЛНЕНИЯ (WINDOW) ---
window.runCode = function () {
    const pId = localStorage.getItem("current_project_id");
    printToTerminal("> Запуск компиляции...");
    apiFetch(`${NGROK_URL}/api/projects/${pId}/execute`, {
        method: 'POST', body: JSON.stringify({ Code: editor.getValue() })
    }).then(data => {
        const out = data.terminalOutput || "Выполнено.";
        if (connection?.state === "Connected") connection.invoke("SendTerminalOutput", pId.toString(), out);
        else printToTerminal(out);
    });
}

window.analyzeCode = async function () {
    const pId = localStorage.getItem("current_project_id");
    const aiBox = document.getElementById('ai-suggestions');
    aiBox.innerHTML = '<div class="text-info small">AI анализирует...</div>';
    try {
        const data = await apiFetch(`${NGROK_URL}/api/projects/${pId}/review`, {
            method: 'POST', body: JSON.stringify({ Code: editor.getValue() })
        });
        aiBox.innerHTML = `<div class="ai-card" style="border-left-color:var(--color-ai); padding:10px;">${data.suggestion}</div>`;
    } catch (err) { aiBox.innerHTML = "Ошибка AI."; }
}

function printToTerminal(msg) {
    const out = document.getElementById('terminal-output');
    if (out) { out.innerHTML += `<div>${msg}</div>`; out.scrollTop = out.scrollHeight; }
}

window.logout = () => { localStorage.clear(); location.reload(); };

// --- 6. ТВОЕ МАСШТАБИРОВАНИЕ (СОХРАНЕНО) ---
function initResizers() {
    const resH = document.getElementById('resizer-h');
    const side = document.getElementById('side-panel');
    const resV = document.getElementById('resizer-v');
    const term = document.getElementById('terminal-panel');
    const editorDiv = document.getElementById('editor');

    if (resH && side) {
        resH.onmousedown = function (e) {
            e.preventDefault();
            if (editorDiv) editorDiv.style.pointerEvents = 'none';
            document.body.style.cursor = 'col-resize';
            document.body.classList.add('resizing-active');
            function doH(m) {
                let newWidth = window.innerWidth - m.clientX;
                newWidth = Math.max(250, Math.min(newWidth, window.innerWidth * 0.6));
                side.style.width = newWidth + 'px';
                side.style.flex = `0 0 ${newWidth}px`;
                if (editor) editor.layout();
            }
            function stopH() {
                if (editorDiv) editorDiv.style.pointerEvents = 'auto';
                document.body.style.cursor = '';
                document.body.classList.remove('resizing-active');
                document.removeEventListener('mousemove', doH);
                document.removeEventListener('mouseup', stopH);
            }
            document.addEventListener('mousemove', doH);
            document.addEventListener('mouseup', stopH);
        };
    }
    if (resV && term) {
        resV.onmousedown = function (e) {
            e.preventDefault();
            if (editorDiv) editorDiv.style.pointerEvents = 'none';
            document.body.style.cursor = 'row-resize';
            document.body.classList.add('resizing-active');
            function doV(m) {
                let newHeight = window.innerHeight - m.clientY;
                newHeight = Math.max(40, Math.min(newHeight, window.innerHeight * 0.8));
                term.style.height = newHeight + 'px';
                term.style.flex = `0 0 ${newHeight}px`;
                if (editor) editor.layout();
            }
            function stopV() {
                if (editorDiv) editorDiv.style.pointerEvents = 'auto';
                document.body.style.cursor = '';
                document.body.classList.remove('resizing-active');
                document.removeEventListener('mousemove', doV);
                document.removeEventListener('mouseup', stopV);
            }
            document.addEventListener('mousemove', doV);
            document.addEventListener('mouseup', stopV);
        };
    }
}