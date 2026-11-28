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
  actualModel?: string | null;
  requestCount?: number;
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

export type StoryEntityCategory =
  | 'Character'
  | 'Location'
  | 'Skill'
  | 'Item'
  | 'Organization'
  | 'Concept'
  | 'Honorific'
  | 'Other';

export type FactStatus = 'Unknown' | 'Provisional' | 'Confirmed' | 'Conflicted';

export type SceneFactType = 'Setting' | 'Time' | 'PresentCharacters' | 'EventState' | 'Other';

export interface StoryEntityItem {
  id: string;
  novelId: string;
  targetLanguage: string;
  sourceTerm: string;
  translatedTerm: string;
  category: StoryEntityCategory;
  aliases: string[];
  description?: string | null;
  origin: 'Manual' | 'AiExtracted';
  status: FactStatus;
  conflictDetails?: string | null;
  isLocked: boolean;
  evidenceSegmentId?: string | null;
  evidenceSegmentStableId?: string | null;
  evidenceSegmentOrder?: number | null;
  evidenceQuote?: string | null;
  version: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertStoryEntityPayload {
  targetLanguage: string;
  sourceTerm: string;
  translatedTerm: string;
  category?: StoryEntityCategory;
  aliases?: string[];
  description?: string | null;
  isLocked?: boolean;
  status?: FactStatus;
  evidenceSegmentId?: string | null;
  evidenceQuote?: string | null;
  conflictDetails?: string | null;
  expectedVersion?: number | null;
  clearEvidence?: boolean;
}

export interface SceneFactItem {
  id: string;
  novelId: string;
  targetLanguage: string;
  factKey: string;
  factType: SceneFactType;
  summary: string;
  origin: 'Manual' | 'AiExtracted';
  status: FactStatus;
  conflictDetails?: string | null;
  isLocked: boolean;
  evidenceSegmentId?: string | null;
  evidenceSegmentStableId?: string | null;
  evidenceSegmentOrder?: number | null;
  evidenceQuote?: string | null;
  version: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertSceneFactPayload {
  targetLanguage: string;
  factKey: string;
  summary: string;
  factType?: SceneFactType;
  isLocked?: boolean;
  status?: FactStatus;
  evidenceSegmentId?: string | null;
  evidenceQuote?: string | null;
  expectedVersion?: number | null;
  clearEvidence?: boolean;
}

export interface SpeechObservationItem {
  id: string;
  novelId: string;
  targetLanguage: string;
  speakerName: string;
  addresseeName?: string | null;
  speechRegister: string;
  pronounStyle?: string | null;
  observation: string;
  origin: 'Manual' | 'AiExtracted';
  status: FactStatus;
  conflictDetails?: string | null;
  isLocked: boolean;
  evidenceSegmentId?: string | null;
  evidenceSegmentStableId?: string | null;
  evidenceSegmentOrder?: number | null;
  evidenceQuote?: string | null;
  version: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertSpeechObservationPayload {
  targetLanguage: string;
  speakerName: string;
  speechRegister: string;
  observation: string;
  addresseeName?: string | null;
  pronounStyle?: string | null;
  isLocked?: boolean;
  status?: FactStatus;
  evidenceSegmentId?: string | null;
  evidenceQuote?: string | null;
  expectedVersion?: number | null;
  clearEvidence?: boolean;
}

export interface ExtractStoryMemoryPayload {
  targetLanguage: string;
  provider: 'gemini' | 'openai' | 'mock';
  model?: string;
  startOrder?: number | null;
  endOrder?: number | null;
  apiKey?: string;
}

export interface ExtractStoryMemoryResponse {
  created: number;
  updated: number;
  skippedLocked: number;
  conflicted: number;
  extractedCount: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  modelUsed: string;
  glossary: StoryEntityItem[];
  sceneFacts: SceneFactItem[];
  speechObservations: SpeechObservationItem[];
}

export interface StoryConsistencyIssue {
  segmentOrder: number;
  entityId: string;
  sourceTerm: string;
  expectedTranslation: string;
  category: string;
  matchedSourceTerms: string[];
}

export interface StoryConsistencyReport {
  jobId: string;
  novelId: string;
  targetLanguage: string;
  checkedSegments: number;
  glossaryCount: number;
  issueCount: number;
  issues: StoryConsistencyIssue[];
}

async function readApiError(res: Response, fallback: string): Promise<string> {
  try {
    const raw = await res.text();
    if (!raw) return `${fallback} (${res.status})`;
    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed.error === 'string' && parsed.error.trim()) {
      return parsed.error;
    }
    return raw;
  } catch {
    return `${fallback} (${res.status})`;
  }
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
  model?: string,
  apiKey?: string
): Promise<TranslationJobSummary> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/translations`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      [headerName]: token,
    },
    body: JSON.stringify({
      novelId,
      targetLanguage,
      provider,
      model,
      apiKey: apiKey || undefined,
    }),
  });
  if (!res.ok) {
    const err = await res.text().catch(() => 'Failed to submit translation');
    throw new Error(err);
  }
  return res.json();
}

export async function setJobApiKey(
  jobId: string,
  apiKey: string
): Promise<{ id: string; status: string }> {
  const { token, headerName } = await getAntiforgeryToken();
  const res = await fetch(`${API_BASE}/translations/${encodeURIComponent(jobId)}/api-key`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      [headerName]: token,
    },
    body: JSON.stringify({ apiKey }),
  });
  if (!res.ok) {
    const err = await res.text().catch(() => 'Failed to set API key for job');
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

// Story Memory Endpoints (BASI-004)
async function sendStoryMutation<T>(
  path: string,
  method: 'POST' | 'PUT' | 'DELETE',
  fallbackError: string,
  body?: unknown
): Promise<T> {
  const { token, headerName } = await getAntiforgeryToken();
  const headers: Record<string, string> = { [headerName]: token };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  const res = await fetch(`${API_BASE}${path}`, {
    method,
    credentials: 'include',
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(await readApiError(res, fallbackError));
  return res.json();
}

export interface StoryMemoryQueryFilters {
  targetLanguage?: string;
  category?: string;
  factType?: string;
  status?: string;
  search?: string;
  startOrder?: number;
  endOrder?: number;
  evidenceSegmentId?: string;
}

function buildStoryMemoryQueryString(opts?: string | StoryMemoryQueryFilters): string {
  const filters: StoryMemoryQueryFilters = typeof opts === 'string' ? { targetLanguage: opts } : (opts ?? {});
  const params = new URLSearchParams();
  if (filters.targetLanguage) params.set('targetLanguage', filters.targetLanguage);
  if (filters.category) params.set('category', filters.category);
  if (filters.factType) params.set('factType', filters.factType);
  if (filters.status) params.set('status', filters.status);
  if (filters.search) params.set('search', filters.search);
  if (filters.startOrder !== undefined) params.set('startOrder', String(filters.startOrder));
  if (filters.endOrder !== undefined) params.set('endOrder', String(filters.endOrder));
  if (filters.evidenceSegmentId) params.set('evidenceSegmentId', filters.evidenceSegmentId);
  const qs = params.toString();
  return qs ? `?${qs}` : '';
}

export async function getStoryMemory(
  novelId: string,
  opts?: StoryMemoryQueryFilters
): Promise<StoryEntityItem[]> {
  const url = `${API_BASE}/novels/${encodeURIComponent(novelId)}/story-memory${buildStoryMemoryQueryString(opts)}`;
  const res = await fetch(url, { credentials: 'include' });
  if (!res.ok) throw new Error(await readApiError(res, 'Failed to fetch story memory'));
  return res.json();
}

export const createStoryEntity = (novelId: string, payload: UpsertStoryEntityPayload) =>
  sendStoryMutation<StoryEntityItem>(`/novels/${encodeURIComponent(novelId)}/story-memory`, 'POST', 'Failed to create story entity', payload);

export const updateStoryEntity = (novelId: string, entryId: string, payload: UpsertStoryEntityPayload) =>
  sendStoryMutation<StoryEntityItem>(`/novels/${encodeURIComponent(novelId)}/story-memory/${encodeURIComponent(entryId)}`, 'PUT', 'Failed to update story entity', payload);

export const deleteStoryEntity = (novelId: string, entryId: string) =>
  sendStoryMutation<{ deleted: boolean; id: string }>(`/novels/${encodeURIComponent(novelId)}/story-memory/${encodeURIComponent(entryId)}`, 'DELETE', 'Failed to delete story entity');

export async function getSceneFacts(
  novelId: string,
  opts?: string | StoryMemoryQueryFilters
): Promise<SceneFactItem[]> {
  const res = await fetch(`${API_BASE}/novels/${encodeURIComponent(novelId)}/scene-facts${buildStoryMemoryQueryString(opts)}`, { credentials: 'include' });
  if (!res.ok) throw new Error(await readApiError(res, 'Failed to fetch scene facts'));
  return res.json();
}

export const createSceneFact = (novelId: string, payload: UpsertSceneFactPayload) =>
  sendStoryMutation<SceneFactItem>(`/novels/${encodeURIComponent(novelId)}/scene-facts`, 'POST', 'Failed to create scene fact', payload);

export const updateSceneFact = (novelId: string, factId: string, payload: UpsertSceneFactPayload) =>
  sendStoryMutation<SceneFactItem>(`/novels/${encodeURIComponent(novelId)}/scene-facts/${encodeURIComponent(factId)}`, 'PUT', 'Failed to update scene fact', payload);

export const deleteSceneFact = (novelId: string, factId: string) =>
  sendStoryMutation<{ deleted: boolean; id: string }>(`/novels/${encodeURIComponent(novelId)}/scene-facts/${encodeURIComponent(factId)}`, 'DELETE', 'Failed to delete scene fact');

export async function getSpeechObservations(
  novelId: string,
  opts?: string | StoryMemoryQueryFilters
): Promise<SpeechObservationItem[]> {
  const res = await fetch(`${API_BASE}/novels/${encodeURIComponent(novelId)}/speech-observations${buildStoryMemoryQueryString(opts)}`, { credentials: 'include' });
  if (!res.ok) throw new Error(await readApiError(res, 'Failed to fetch speech observations'));
  return res.json();
}

export const createSpeechObservation = (novelId: string, payload: UpsertSpeechObservationPayload) =>
  sendStoryMutation<SpeechObservationItem>(`/novels/${encodeURIComponent(novelId)}/speech-observations`, 'POST', 'Failed to create speech observation', payload);

export const updateSpeechObservation = (novelId: string, obsId: string, payload: UpsertSpeechObservationPayload) =>
  sendStoryMutation<SpeechObservationItem>(`/novels/${encodeURIComponent(novelId)}/speech-observations/${encodeURIComponent(obsId)}`, 'PUT', 'Failed to update speech observation', payload);

export const deleteSpeechObservation = (novelId: string, obsId: string) =>
  sendStoryMutation<{ deleted: boolean; id: string }>(`/novels/${encodeURIComponent(novelId)}/speech-observations/${encodeURIComponent(obsId)}`, 'DELETE', 'Failed to delete speech observation');

export const extractStoryMemory = (novelId: string, payload: ExtractStoryMemoryPayload) =>
  sendStoryMutation<ExtractStoryMemoryResponse>(
    `/novels/${encodeURIComponent(novelId)}/story-memory/extract`,
    'POST',
    'Failed to extract story memory',
    { ...payload, apiKey: payload.apiKey || undefined }
  );

export async function getJobConsistency(jobId: string): Promise<StoryConsistencyReport> {
  const res = await fetch(`${API_BASE}/translations/${encodeURIComponent(jobId)}/consistency`, {
    credentials: 'include',
  });
  if (!res.ok) throw new Error(await readApiError(res, 'Failed to check consistency'));
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
