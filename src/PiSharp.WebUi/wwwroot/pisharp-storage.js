const dbName = "pisharp-webui";
const storeName = "sessions";
const dbVersion = 2;

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

export async function saveSession(sessionId, payload, metadataJson) {
    const db = await openDb();
    const metadata = metadataJson ? JSON.parse(metadataJson) : {};
    const now = new Date().toISOString();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readwrite");
        const store = transaction.objectStore(storeName);
        const request = store.put({
            sessionId,
            payload,
            title: metadata.title ?? sessionId,
            modelId: metadata.modelId ?? null,
            createdAt: metadata.createdAt ?? now,
            updatedAt: metadata.updatedAt ?? now
        });

        request.onsuccess = () => resolve();
        request.onerror = () => reject(request.error ?? new Error("Failed to save session."));
    });
}

export async function loadSession(sessionId) {
    const row = await getSessionRow(sessionId);
    return row ? row.payload : null;
}

export async function loadSessionRecord(sessionId) {
    const row = await getSessionRow(sessionId);

    if (!row) {
        return null;
    }

    return JSON.stringify({
        metadata: toMetadata(row),
        messages: row.payload ? JSON.parse(row.payload) : []
    });
}

export async function getSessionMetadata(sessionId) {
    const row = await getSessionRow(sessionId);
    return row ? JSON.stringify(toMetadata(row)) : null;
}

export async function listSessions() {
    const rows = await getAllSessionRows();
    return rows.map((row) => row.sessionId);
}

export async function searchSessions(queryJson) {
    const rows = await getAllSessionRows();
    const query = queryJson ? JSON.parse(queryJson) : {};
    const titleFilter = (query.titleContains ?? "").trim().toLowerCase();
    const modelFilter = (query.modelId ?? "").trim().toLowerCase();

    const filtered = rows.filter((row) => {
        const title = (row.title ?? row.sessionId ?? "").toLowerCase();
        const modelId = (row.modelId ?? "").toLowerCase();
        return (!titleFilter || title.includes(titleFilter))
            && (!modelFilter || modelId === modelFilter);
    });

    return JSON.stringify(filtered.map((row) => toMetadata(row)));
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

async function getSessionRow(sessionId) {
    const db = await openDb();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readonly");
        const store = transaction.objectStore(storeName);
        const request = store.get(sessionId);

        request.onsuccess = () => resolve(request.result ?? null);
        request.onerror = () => reject(request.error ?? new Error("Failed to load session."));
    });
}

async function getAllSessionRows() {
    const db = await openDb();

    return new Promise((resolve, reject) => {
        const transaction = db.transaction(storeName, "readonly");
        const store = transaction.objectStore(storeName);
        const request = store.getAll();

        request.onsuccess = () => {
            const rows = Array.isArray(request.result) ? request.result : [];
            rows.sort((left, right) => {
                const leftValue = Date.parse(left.updatedAt ?? left.createdAt ?? 0);
                const rightValue = Date.parse(right.updatedAt ?? right.createdAt ?? 0);
                return rightValue - leftValue;
            });

            resolve(rows);
        };

        request.onerror = () => reject(request.error ?? new Error("Failed to list sessions."));
    });
}

function toMetadata(row) {
    const createdAt = row.createdAt ?? new Date().toISOString();
    const updatedAt = row.updatedAt ?? createdAt;

    return {
        sessionId: row.sessionId,
        title: row.title ?? row.sessionId,
        modelId: row.modelId ?? null,
        createdAt,
        updatedAt
    };
}
