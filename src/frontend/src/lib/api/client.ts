const API_BASE = '/api';

export interface HealthResponse {
  status: string;
  timestamp: string;
  version: string;
  database: string;
}

export async function checkHealth(): Promise<HealthResponse> {
  const res = await fetch(`${API_BASE}/health`);
  if (!res.ok) throw new Error(`Health check failed: ${res.status}`);
  return res.json();
}

// Generic fetch wrapper with error handling
export async function apiFetch<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
    credentials: 'include',
  });

  if (!res.ok) {
    const error = await res.text().catch(() => 'Unknown error');
    throw new Error(`API Error ${res.status}: ${error}`);
  }

  return res.json();
}
