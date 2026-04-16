const dbName = "pisharp-webui";
const storeName = "sessions";
const dbVersion = 1;

export function openDb() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(dbName, dbVersion);

        request.onupgradeneeded = () => {
            const db = request.result;
            if (!db.objectStoreNames.contains(storeName)) {
                db.createObjectStore(storeName, { keyPath: "sessionId" });
            }
        };

        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error ?? new Error("Failed to open IndexedDB."));
    });
}

export async function saveSession(sessionId, payload) {
    const db = await openDb();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readwrite");
        const store = transaction.objectStore(storeName);
        const request = store.put({
            sessionId,
            payload,
            updatedAt: Date.now()
        });

        request.onsuccess = () => resolve();
        request.onerror = () => reject(request.error ?? new Error("Failed to save session."));
    });
}

export async function loadSession(sessionId) {
    const db = await openDb();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readonly");
        const store = transaction.objectStore(storeName);
        const request = store.get(sessionId);

        request.onsuccess = () => resolve(request.result ? request.result.payload : null);
        request.onerror = () => reject(request.error ?? new Error("Failed to load session."));
    });
}

export async function listSessions() {
    const db = await openDb();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readonly");
        const store = transaction.objectStore(storeName);
        const request = store.getAll();

        request.onsuccess = () => {
            const rows = Array.isArray(request.result) ? request.result : [];
            rows.sort((left, right) => (right.updatedAt ?? 0) - (left.updatedAt ?? 0));
            resolve(rows.map((row) => row.sessionId));
        };

        request.onerror = () => reject(request.error ?? new Error("Failed to list sessions."));
    });
}

export async function deleteSession(sessionId) {
    const db = await openDb();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readwrite");
        const store = transaction.objectStore(storeName);
        const request = store.delete(sessionId);

        request.onsuccess = () => resolve();
        request.onerror = () => reject(request.error ?? new Error("Failed to delete session."));
    });
}
