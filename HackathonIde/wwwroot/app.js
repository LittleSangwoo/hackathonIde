const NGROK_URL = "https://tova-seminivorous-lavona.ngrok-free.dev";
let editor;
const projectId = "1";
const userName = "Student_Dev";
let timeout; // Переменная для таймера задержки

// 1. Инициализация Monaco Editor
require.config({ paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs' } });
require(['vs/editor/editor.main'], function () {
    editor = monaco.editor.create(document.getElementById('editor'), {
        value: "// Напишите ваш C# код здесь...\n",
        language: 'csharp',
        theme: 'vs-dark',
        automaticLayout: true
    });

    // ОПТИМИЗИРОВАННАЯ отправка кода (с задержкой 500мс)
    editor.onDidChangeModelContent(() => {
        clearTimeout(timeout); // Сбрасываем таймер при каждом нажатии клавиши

        timeout = setTimeout(() => {
            const currentCode = editor.getValue();

            // ВАЖНО: передаем projectId и currentCode, как требует EditorHub.cs
            if (connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("BroadcastCodeChange", projectId, currentCode)
                    .catch(err => console.error("Ошибка синхронизации:", err));
                console.log("Данные отправлены на сервер после паузы в печати");
            }
        }, 500);
    });
});

// 2. Настройка SignalR
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${NGROK_URL}/editorHub`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets
    })
    .withAutomaticReconnect()
    .build();

// Слушаем обновления от напарника
connection.on("ReceiveCodeUpdate", (newCode) => {
    // Обновляем только если код действительно изменился (чтобы не дергался курсор)
    if (editor && newCode !== editor.getValue()) {
        const position = editor.getPosition();
        editor.setValue(newCode);
        editor.setPosition(position); // Возвращаем курсор на место
    }
});

// Слушаем системные события
connection.on("ReceiveSystemEvent", (message) => {
    const logs = document.getElementById('system-logs');
    if (logs) {
        logs.innerHTML += `<div class="system-msg">[${new Date().toLocaleTimeString()}] ${message}</div>`;
        logs.scrollTop = logs.scrollHeight;
    }
});

// 3. Функция вызова GigaChat
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

// 4. Запуск кода
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
        // Входим в сессию (передаем ID и Имя)
        await connection.invoke("JoinProjectSession", projectId, userName);
    } catch (err) {
        console.log("Error starting SignalR", err);
        setTimeout(start, 5000);
    }
}

start();