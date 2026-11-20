const API_BASE = '/api';

export interface HealthResponse {
  status: string;
  timestamp: string;
  version: string;
  database: string;
}

export interface UserResponse {
  id: string;
  email: string;
  displayName: string;
  createdAt: string;
}

export interface UploadResponse {
  novelId: string;
  title: string;
  sourceLanguage: string;
  segmentCount: number;
  uploadedAt: string;
}

export interface AntiforgeryTokenResponse {
  token: string;
  headerName: string;
}

export async function checkHealth(): Promise<HealthResponse> {
  const res = await fetch(`${API_BASE}/health`);
  if (!res.ok) throw new Error(`Health check failed: ${res.status}`);
  return res.json();
}

export async function getAntiforgeryToken(): Promise<AntiforgeryTokenResponse> {
  const res = await fetch(`${API_BASE}/antiforgery/token`, { credentials: 'include' });
  if (!res.ok) throw new Error('Failed to fetch antiforgery token');
  return res.json();
}

export async function getCurrentUser(): Promise<UserResponse | null> {
  const res = await fetch(`${API_BASE}/auth/me`, { credentials: 'include' });
  if (res.status === 401) return null;
  if (!res.ok) throw new Error(`Failed to get current user: ${res.status}`);
  return res.json();
}

export async function logout(): Promise<void> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/auth/logout`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      [headerName]: token,
    },
  });
  if (!res.ok) {
    const text = await res.text().catch(() => 'Logout failed');
    throw new Error(text);
  }
}

export async function uploadNovel(
  file: File,
  title: string,
  sourceLanguage: string
): Promise<UploadResponse> {
  const { token, headerName } = await getAntiforgeryToken();
  const formData = new FormData();
  formData.append('file', file);
  formData.append('title', title);
  formData.append('sourceLanguage', sourceLanguage);

  const res = await fetch(`${API_BASE}/novels/upload`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      [headerName]: token,
    },
    body: formData,
  });

  if (!res.ok) {
    const errorText = await res.text().catch(() => 'Upload failed');
    throw new Error(errorText || `Upload failed with status ${res.status}`);
  }

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
