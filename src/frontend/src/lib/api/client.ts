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

export interface ApiKeyStatus {
  [provider: string]: boolean;
}

export interface SetApiKeyResponse {
  success: boolean;
  provider: string;
}

export interface NovelSummary {
  id: string;
  title: string;
  sourceLanguage: string;
  segmentCount: number;
  uploadedAt: string;
}

export interface TranslationJobSummary {
  id: string;
  novelId: string;
  targetLanguage: string;
  provider: string;
  model: string;
  status: 'Created' | 'Queued' | 'Processing' | 'WaitingForApiKey' | 'Completed' | 'Failed' | 'Cancelled';
  totalSegments: number;
  completedSegments: number;
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
  errorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface TranslatedSegmentItem {
  id: string;
  segmentId: string;
  order: number;
  sourceText: string;
  translatedText?: string | null;
  status: 'Pending' | 'Translating' | 'Completed' | 'Failed';
  errorMessage?: string | null;
}

export interface TranslationJobDetail extends TranslationJobSummary {
  segments: TranslatedSegmentItem[];
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

// BYOK Key Vault Endpoints (ADR-004)
export async function getApiKeyStatus(): Promise<ApiKeyStatus> {
  const res = await fetch(`${API_BASE}/auth/api-keys`, { credentials: 'include' });
  if (!res.ok) throw new Error('Failed to get API key status');
  return res.json();
}

export async function setApiKey(provider: string, apiKey: string): Promise<SetApiKeyResponse> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/auth/api-keys`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      [headerName]: token,
    },
    body: JSON.stringify({ provider, apiKey }),
  });
  if (!res.ok) {
    const err = await res.text().catch(() => 'Failed to save API key');
    throw new Error(err);
  }
  return res.json();
}

export async function deleteApiKey(provider: string): Promise<{ success: boolean; provider: string }> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/auth/api-keys/${encodeURIComponent(provider)}`, {
    method: 'DELETE',
    credentials: 'include',
    headers: {
      [headerName]: token,
    },
  });
  if (!res.ok) {
    const err = await res.text().catch(() => 'Failed to delete API key');
    throw new Error(err);
  }
  return res.json();
}

// Novels Endpoints
export async function getNovels(): Promise<NovelSummary[]> {
  const res = await fetch(`${API_BASE}/novels`, { credentials: 'include' });
  if (!res.ok) throw new Error('Failed to fetch novels');
  return res.json();
}

// Translation Endpoints (BASI-003)
export async function createTranslationJob(
  novelId: string,
  targetLanguage: string,
  provider: string,
  model?: string
): Promise<TranslationJobSummary> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/translations`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      [headerName]: token,
    },
    body: JSON.stringify({ novelId, targetLanguage, provider, model }),
  });
  if (!res.ok) {
    const err = await res.text().catch(() => 'Failed to submit translation');
    throw new Error(err);
  }
  return res.json();
}

export async function getTranslationJobs(): Promise<TranslationJobSummary[]> {
  const res = await fetch(`${API_BASE}/translations`, { credentials: 'include' });
  if (!res.ok) throw new Error('Failed to fetch translation jobs');
  return res.json();
}

export async function getTranslationJob(id: string): Promise<TranslationJobDetail> {
  const res = await fetch(`${API_BASE}/translations/${encodeURIComponent(id)}`, { credentials: 'include' });
  if (!res.ok) throw new Error('Failed to fetch translation job details');
  return res.json();
}

export async function cancelTranslationJob(id: string): Promise<{ id: string; status: string }> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/translations/${encodeURIComponent(id)}/cancel`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      [headerName]: token,
    },
  });
  if (!res.ok) {
    const err = await res.text().catch(() => 'Failed to cancel translation job');
    throw new Error(err);
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
