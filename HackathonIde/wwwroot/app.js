const NGROK_URL = "https://tova-seminivorous-lavona.ngrok-free.dev";
let editor;
const projectId = "1"; // Используем ID 1, как в эндпоинтах напарника
const userName = "Student_Dev";

// 1. Инициализация Monaco Editor
require.config({ paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs' } });
require(['vs/editor/editor.main'], function () {
    editor = monaco.editor.create(document.getElementById('editor'), {
        value: "// Напишите ваш C# код здесь...\n",
        language: 'csharp',
        theme: 'vs-dark',
        automaticLayout: true
    });

    // Отправка кода напарнику при изменении
    editor.onDidChangeModelContent(() => {
        const code = editor.getValue();
        connection.invoke("BroadcastCodeChange", projectId, code)
            .catch(err => console.error(err));
    });
});

// 2. Настройка SignalR (через ngrok)
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${NGROK_URL}/editorHub`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets
    })
    .withAutomaticReconnect()
    .build();

// Слушаем обновления от напарника (ReceiveCodeUpdate прописан в EditorHub.cs)
connection.on("ReceiveCodeUpdate", (newCode) => {
    if (editor && newCode !== editor.getValue()) {
        const position = editor.getPosition();
        editor.setValue(newCode);
        editor.setPosition(position);
    }
});

// Слушаем системные события (подключения, сообщения)
connection.on("ReceiveSystemEvent", (message) => {
    const logs = document.getElementById('system-logs');
    logs.innerHTML += `<div class="system-msg">[${new Date().toLocaleTimeString()}] ${message}</div>`;
    logs.scrollTop = logs.scrollHeight;
});

// 3. Функция вызова GigaChat (эндпоинт /review в Program.cs)
async function analyzeCode() {
    const aiBox = document.getElementById('ai-suggestions');
    aiBox.innerHTML = '<div class="spinner-border spinner-border-sm text-primary"></div> AI думает...';

    try {
        const response = await fetch(`${NGROK_URL}/api/projects/${projectId}/review`, {
            method: 'POST'
        });
        const data = await response.json();

        aiBox.innerHTML = `
            <div class="ai-card">
                <strong>GigaChat:</strong><br>
                <small>${data.suggestion}</small>
            </div>`;
    } catch (err) {
        aiBox.innerHTML = '<div class="text-danger">Ошибка связи с AI</div>';
    }
}

// 4. Запуск кода (эндпоинт /execute в Program.cs)
async function runCode() {
    try {
        const response = await fetch(`${NGROK_URL}/api/projects/${projectId}/execute`, {
            method: 'POST'
        });
        const data = await response.json();
        alert("Результат выполнения:\n" + data.terminalOutput);
    } catch (err) {
        alert("Ошибка запуска!");
    }
}

// Старт соединения
async function start() {
    try {
        await connection.start();
        console.log("Connected to SignalR via ngrok");
        // Входим в сессию (метод из EditorHub.cs)
        await connection.invoke("JoinProjectSession", projectId, userName);
    } catch (err) {
        console.log("Error starting SignalR", err);
        setTimeout(start, 5000);
    }
}

start();