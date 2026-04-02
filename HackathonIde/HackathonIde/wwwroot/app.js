const NGROK_URL = "https://tova-seminivorous-lavona.ngrok-free.dev";
let editor, connection, timeout, isLoginMode = true;
let isApplyingRemoteChange = false;
let lastTypingTime = 0;
let pendingAiCode = "";

// Память для хранения декораций и указателей мыши
const remoteCursorsMap = new Map();
const remoteMiceMap = new Map();

// --- ГЕНЕРАТОР УНИКАЛЬНЫХ ЦВЕТОВ КУРСОРОВ ---
const cursorColors = ['#00e5ff', '#33ffaa', '#ffca28', '#ff7373', '#ff66ff', '#b366ff', '#ffd700', '#00ffcc'];
const userColorMap = new Map();

function getUserColorClass(username) {
    const safeName = "user_" + username.replace(/[^a-zA-Z0-9]/g, '_');
    if (!userColorMap.has(username)) {
        // Простой хэш для привязки постоянного цвета к нику
        let hash = 0;
        for (let i = 0; i < username.length; i++) hash = username.charCodeAt(i) + ((hash << 5) - hash);
        const color = cursorColors[Math.abs(hash) % cursorColors.length];
        userColorMap.set(username, safeName);

        // Динамически внедряем стили для этого юзера
        let styleEl = document.getElementById('dynamic-cursor-styles');
        if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = 'dynamic-cursor-styles';
            document.head.appendChild(styleEl);
        }
        styleEl.innerHTML += `
            .remote-cursor-${safeName} { border-left: 2px solid ${color} !important; box-shadow: 0 0 5px ${color} !important; }
            .remote-caret-tag-${safeName} { background: ${color} !important; color: #000 !important; font-size: 10px; font-weight: 800; padding: 1px 4px; border-radius: 2px; position: absolute; transform: translateY(-100%); white-space: nowrap; z-index: 100; }
            .remote-mouse-pointer-${safeName} { background: ${color} !important; border-color: #fff !important; }
            .remote-mouse-pointer-${safeName}::after { background: ${color} !important; color: #000 !important; }
        `;
    }
    return safeName;
}

// --- 1. УТИЛИТЫ (УВЕДОМЛЕНИЯ И МОДАЛКИ) ---
window.showNotification = function (message, type = "info") {
    const container = document.getElementById('notification-container');
    if (!container) return;
    const toast = document.createElement('div');
    toast.className = `win-notification ${type}`;
    const titles = { error: 'Ошибка', success: 'Успешно', info: 'Уведомление' };
    const icons = { error: 'bi-exclamation-octagon-fill text-danger', success: 'bi-check-circle-fill text-success', info: 'bi-info-circle-fill text-info' };
    toast.innerHTML = `<i class="bi ${icons[type] || icons.info}"></i><div class="win-content"><div class="win-title">${titles[type]}</div><div class="win-msg">${message}</div></div><button class="win-close" onclick="this.parentElement.remove()">&times;</button>`;
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
    if (!response.ok) { showNotification(data.message || "Ошибка", "error"); throw new Error(data.message); }
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
        // Показываем кнопки редактора, включая стрелочки
        ['btn-header-delete', 'btn-header-leave', 'btn-header-save', 'btn-header-undo', 'btn-header-redo', 'header-divider-ide'].forEach(id => {
            const el = document.getElementById(id); if (el) el.style.display = isIde ? 'flex' : 'none';
        });
    }
    if (screenId === 'screen-ide' && editor) {
        setTimeout(() => { editor.layout(); editor.focus(); }, 100);
    }
}

// --- 4. SIGNALR ---
function startSignalR(projectId) {
    const token = localStorage.getItem("jwt_token");
    if (connection) connection.stop();

    connection = new signalR.HubConnectionBuilder()
        .withUrl(`${NGROK_URL}/editorHub`, { accessTokenFactory: () => token, transport: signalR.HttpTransportType.WebSockets })
        .withAutomaticReconnect().build();

    // ПРИЕМ КОДА
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
        } finally { setTimeout(() => { isApplyingRemoteChange = false; }, 30); }
    });

    // ПРИЕМ ТЕКСТОВОГО КУРСОРОВ (С уникальным цветом)
    connection.on("ReceiveCursor", (remoteUser, lineNumber, column) => {
        if (!editor) return;
        const myUser = localStorage.getItem("username");
        if (remoteUser === myUser) return;

        const safeName = getUserColorClass(remoteUser);
        const oldDecorations = remoteCursorsMap.get(remoteUser) || [];
        const newDecorations = [
            { range: new monaco.Range(lineNumber, column, lineNumber, column), options: { className: `remote-cursor-${safeName}` } },
            { range: new monaco.Range(lineNumber, column, lineNumber, column), options: { after: { content: remoteUser, inlineClassName: `remote-caret-tag-${safeName}` } } }
        ];
        remoteCursorsMap.set(remoteUser, editor.deltaDecorations(oldDecorations, newDecorations));
    });

    // ПРИЕМ ДВИЖЕНИЯ МЫШИ (С уникальным цветом)
    connection.on("ReceiveMousePosition", (remoteUser, percentX, percentY) => {
        if (!editor) return;
        const myUser = localStorage.getItem("username");
        if (remoteUser === myUser) return;

        let mouseEl = remoteMiceMap.get(remoteUser);
        if (!mouseEl) {
            const safeName = getUserColorClass(remoteUser);
            mouseEl = document.createElement('div');
            mouseEl.className = `remote-mouse-pointer remote-mouse-pointer-${safeName}`;
            mouseEl.setAttribute('data-user', remoteUser);
            document.getElementById('editor').appendChild(mouseEl);
            remoteMiceMap.set(remoteUser, mouseEl);
        }

        const rect = document.getElementById('editor').getBoundingClientRect();
        mouseEl.style.display = 'block';
        mouseEl.style.left = `${percentX * rect.width}px`;
        mouseEl.style.top = `${percentY * rect.height}px`;
    });

    // СПИСОК УЧАСТНИКОВ
    // СПИСОК УЧАСТНИКОВ (С группировкой сессий)
    connection.on("UpdateUserList", (users) => {
        const list = document.getElementById('user-list');
        if (list) {
            // 1. Группируем пользователей по имени и считаем количество их сессий
            const userCounts = {};
            const uniqueUsers = [];

            users.forEach(u => {
                if (!userCounts[u.username]) {
                    userCounts[u.username] = 0;
                    uniqueUsers.push(u); // Сохраняем первого для аватарки
                }
                userCounts[u.username]++;
            });

            // 2. Отрисовываем сгруппированный список
            list.innerHTML = uniqueUsers.map(u => {
                const count = userCounts[u.username];
                // Если сессий > 1, генерируем серую приписку мелким шрифтом
                const sessionBadge = count > 1
                    ? `<span style="font-size: 10px; color: #888; margin-left: 6px;">(сессий: ${count})</span>`
                    : '';

                return `
                <div class="user-list-item d-flex align-items-center p-2">
                    <img src="${u.avatarUrl}" style="width:22px; border-radius:50%; margin-right:8px">
                    <span class="small text-light">${u.username}</span>
                    ${sessionBadge}
                </div>
                `;
            }).join('');
        }

        // 3. Очищаем курсоры вышедших игроков, чтобы они не зависали на экране
        const activeNames = users.map(u => u.username);
        remoteCursorsMap.forEach((ids, name) => {
            if (!activeNames.includes(name)) {
                if (editor) editor.deltaDecorations(ids, []);
                remoteCursorsMap.delete(name);
            }
        });
        remoteMiceMap.forEach((el, name) => {
            if (!activeNames.includes(name)) {
                el.remove();
                remoteMiceMap.delete(name);
            }
        });
    });

    connection.on("ReceiveAiResolution", (resolvedCode) => {
        pendingAiCode = resolvedCode;
        document.getElementById('ai-resolved-code').innerText = resolvedCode;
        document.getElementById('ai-merge-modal').style.display = 'flex';
    });

    connection.on("ReceiveTerminalOutput", (o) => printToTerminal(o));
    connection.on("ReceiveSystemEvent", (m) => {
        const l = document.getElementById('system-logs');
        if (l) { l.innerHTML += `<div>[${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}] >> ${m}</div>`; l.scrollTop = l.scrollHeight; }
    });

    connection.start().then(() => {
        const st = document.getElementById('connection-status');
        if (st) { st.innerText = "Online"; st.className = "text-success fw-bold"; }
        connection.invoke("JoinProjectSession", projectId.toString());
    });
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
            event.changes.forEach(change => {
                if (connection?.state === "Connected") {
                    connection.invoke("BroadcastCodeChange", pId.toString(), change, editor.getValue());
                }
            });
        });

        editor.onDidChangeCursorPosition((e) => {
            if (connection?.state === "Connected") {
                const pId = localStorage.getItem("current_project_id");
                connection.invoke("BroadcastCursor", pId.toString(), localStorage.getItem("username"), e.position.lineNumber, e.position.column);
            }
        });

        // ОТПРАВКА ДВИЖЕНИЯ МЫШИ
        let lastMouseTime = 0;
        document.getElementById('editor').addEventListener('mousemove', (e) => {
            if (connection?.state === "Connected") {
                const now = Date.now();
                if (now - lastMouseTime > 50) {
                    lastMouseTime = now;
                    const pId = localStorage.getItem("current_project_id");
                    const rect = document.getElementById('editor').getBoundingClientRect();
                    const percentX = (e.clientX - rect.left) / rect.width;
                    const percentY = (e.clientY - rect.top) / rect.height;

                    if (percentX >= 0 && percentX <= 1 && percentY >= 0 && percentY <= 1) {
                        connection.invoke("BroadcastMousePosition", pId.toString(), localStorage.getItem("username"), percentX, percentY);
                    }
                }
            }
        });

        initResizers();
    });
}

// --- 6. ИСТОРИЯ (UNDO / REDO) - ФИКС КНОПОК ---
window.sendUndo = () => {
    if (editor) {
        editor.trigger('keyboard', 'undo', null); // Заставляем редактор отменить действие
        editor.focus();
    }
};
window.sendRedo = () => {
    if (editor) {
        editor.trigger('keyboard', 'redo', null); // Заставляем редактор вернуть действие
        editor.focus();
    }
};

// --- 7. КОНФЛИКТЫ ---
window.simulateConflict = () => {
    const pId = localStorage.getItem("current_project_id");
    const codeA = editor.getValue();
    const codeB = codeA + "\n// Remote conflict change simulation";
    if (connection?.state === "Connected") {
        showNotification("AI анализирует конфликт...", "info");
        connection.invoke("TriggerAiConflictResolution", pId.toString(), codeA, codeB);
    }
};

window.applyAiResolution = () => {
    isApplyingRemoteChange = true;
    editor.setValue(pendingAiCode);
    isApplyingRemoteChange = false;
    document.getElementById('ai-merge-modal').style.display = 'none';
    showNotification("Изменения AI приняты", "success");
};
window.closeAiModal = () => document.getElementById('ai-merge-modal').style.display = 'none';

// --- 8. АНАЛИЗ AI ---
window.analyzeCode = async () => {
    const box = document.getElementById('ai-suggestions');
    const pId = localStorage.getItem("current_project_id");
    const tempId = "ai-load-" + Date.now();

    box.innerHTML += `<div id="${tempId}" class="ai-msg-block"><div class="ai-msg-header"><i class="bi bi-robot"></i> AI</div>Анализирую код...</div>`;
    box.scrollTop = box.scrollHeight;

    try {
        const d = await apiFetch(`${NGROK_URL}/api/projects/${pId}/review`, { method: 'POST', body: JSON.stringify({ Code: editor.getValue() }) });
        document.getElementById(tempId).remove();
        box.innerHTML += `<div class="ai-msg-block"><div class="ai-msg-header"><i class="bi bi-robot"></i> AI Assistant</div>${d.suggestion}</div>`;
        box.scrollTop = box.scrollHeight;
    } catch (e) {
        if (document.getElementById(tempId)) document.getElementById(tempId).innerText = "Ошибка анализа.";
    }
}

// --- ОСТАЛЬНОЕ ---
window.handleAuth = async function () {
    const u = document.getElementById("auth-username").value.trim();
    const p = document.getElementById("auth-password").value;
    const ep = isLoginMode ? "/api/auth/login" : "/api/auth/register";
    try {
        const d = await apiFetch(`${NGROK_URL}${ep}`, { method: "POST", body: JSON.stringify({ Username: u, Password: p }) });
        if (isLoginMode) {
            localStorage.setItem("jwt_token", d.token); localStorage.setItem("username", d.username);
            document.getElementById("user-display").innerText = d.username; window.switchScreen("screen-lobby");
        } else { showNotification("Успешно! Войдите.", "success"); window.toggleAuthMode(); }
    } catch (e) { }
}

window.toggleAuthMode = (e) => { if (e) e.preventDefault(); isLoginMode = !isLoginMode; document.getElementById('btn-main-auth').innerText = isLoginMode ? "ВОЙТИ" : "СОЗДАТЬ"; }

window.handleCreateProject = async () => {
    const n = document.getElementById("create-name").value; const p = document.getElementById("create-pass").value;
    try { const d = await apiFetch(`${NGROK_URL}/api/projects/create`, { method: "POST", body: JSON.stringify({ Name: n, Password: p }) }); await enterRoom(d.projectId, p); } catch (e) { }
}

window.handleJoinProject = async () => { await enterRoom(document.getElementById("join-id").value, document.getElementById("join-pass").value); }

async function enterRoom(projectId, password) {
    try {
        const d = await apiFetch(`${NGROK_URL}/api/projects/${projectId}/join`, { method: "POST", body: JSON.stringify({ Password: password }) });
        localStorage.setItem("current_project_id", projectId); localStorage.setItem("current_project_pass", password);
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
    if (connection?.state === "Connected") {
        await connection.invoke("BroadcastCodeChange", pId.toString(), "save", editor.getValue());
        showNotification("Сохранено в базу", "success");
    }
}

window.runCode = function () {
    const pId = localStorage.getItem("current_project_id");
    apiFetch(`${NGROK_URL}/api/projects/${pId}/execute`, { method: 'POST', body: JSON.stringify({ Code: editor.getValue() }) })
        .then(d => { if (connection?.state === "Connected") connection.invoke("SendTerminalOutput", pId.toString(), d.terminalOutput || "Ок."); else printToTerminal(d.terminalOutput || "Ок."); });
}

function printToTerminal(msg) { const out = document.getElementById('terminal-output'); if (out) { out.innerHTML += `<div>${msg}</div>`; out.scrollTop = out.scrollHeight; } }

window.logout = () => { localStorage.clear(); location.reload(); };

// --- 9. МАСШТАБИРОВАНИЕ ---
function initResizers() {
    const resH = document.getElementById('resizer-h'); const side = document.getElementById('side-panel');
    const resV = document.getElementById('resizer-v'); const term = document.getElementById('terminal-panel');
    const editorDiv = document.getElementById('editor');

    if (resH && side) {
        resH.onmousedown = (e) => {
            e.preventDefault(); document.body.classList.add('resizing-active'); if (editorDiv) editorDiv.style.pointerEvents = 'none';
            const doH = (m) => {
                let nw = window.innerWidth - m.clientX;
                nw = Math.max(200, Math.min(nw, window.innerWidth * 0.7));
                side.style.width = nw + 'px'; side.style.flex = `0 0 ${nw}px`;
                if (editor) editor.layout();
            };
            const stopH = () => { document.body.classList.remove('resizing-active'); if (editorDiv) editorDiv.style.pointerEvents = 'auto'; document.removeEventListener('mousemove', doH); document.removeEventListener('mouseup', stopH); };
            document.addEventListener('mousemove', doH); document.addEventListener('mouseup', stopH);
        };
    }
    if (resV && term) {
        resV.onmousedown = (e) => {
            e.preventDefault(); document.body.classList.add('resizing-active'); if (editorDiv) editorDiv.style.pointerEvents = 'none';
            const doV = (m) => {
                let nh = window.innerHeight - m.clientY;
                nh = Math.max(40, Math.min(nh, window.innerHeight * 0.8));
                term.style.height = nh + 'px'; term.style.flex = `0 0 ${nh}px`;
                if (editor) editor.layout();
            };
            const stopV = () => { document.body.classList.remove('resizing-active'); if (editorDiv) editorDiv.style.pointerEvents = 'auto'; document.removeEventListener('mousemove', doV); document.removeEventListener('mouseup', stopV); };
            document.addEventListener('mousemove', doV); document.addEventListener('mouseup', stopV);
        };
    }
}
// --- ОТКРЫТИЕ И ЗАКРЫТИЕ СПРАВКИ ---
window.showInfoModal = () => document.getElementById('info-modal').style.display = 'flex';
window.closeInfoModal = () => document.getElementById('info-modal').style.display = 'none';

document.addEventListener("DOMContentLoaded", () => {
    const token = localStorage.getItem("jwt_token"); const user = localStorage.getItem("username");
    if (token && user) { document.getElementById("user-display").innerText = user; const lastRoomId = localStorage.getItem("current_project_id"); const lastRoomPass = localStorage.getItem("current_project_pass"); if (lastRoomId && lastRoomPass) enterRoom(lastRoomId, lastRoomPass); else window.switchScreen("screen-lobby"); }
});