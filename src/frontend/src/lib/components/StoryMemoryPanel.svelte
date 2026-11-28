<script lang="ts">
  import { onMount } from 'svelte';
  import {
    getStoryMemory,
    createStoryEntity,
    updateStoryEntity,
    deleteStoryEntity,
    getSceneFacts,
    createSceneFact,
    updateSceneFact,
    deleteSceneFact,
    getSpeechObservations,
    createSpeechObservation,
    updateSpeechObservation,
    deleteSpeechObservation,
    extractStoryMemory,
    getJobConsistency,
    type FactStatus,
    type SceneFactType,
    type StoryEntityCategory,
    type StoryEntityItem,
    type SceneFactItem,
    type SpeechObservationItem,
    type ExtractStoryMemoryResponse,
    type StoryConsistencyReport
  } from '$lib/api/client';

  let {
    novelId,
    novelTitle,
    targetLanguage,
    activeJobId = null
  }: {
    novelId: string;
    novelTitle: string;
    targetLanguage: string;
    activeJobId?: string | null;
  } = $props();

  const CATEGORIES: StoryEntityCategory[] = [
    'Character', 'Location', 'Skill', 'Item', 'Organization', 'Concept', 'Honorific', 'Other'
  ];
  const FACT_TYPES: SceneFactType[] = ['Setting', 'Time', 'PresentCharacters', 'EventState', 'Other'];
  const FACT_STATUSES: FactStatus[] = ['Provisional', 'Confirmed', 'Unknown', 'Conflicted'];

  let activeTab = $state<'entities' | 'scenes' | 'speech'>('entities');

  // Story Memory Lite state
  let entries = $state<StoryEntityItem[]>([]);
  let sceneFacts = $state<SceneFactItem[]>([]);
  let speechObservations = $state<SpeechObservationItem[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let searchQuery = $state('');
  let categoryFilter = $state('');
  let statusFilter = $state('');

  // Extraction state
  let extractProvider = $state<'mock' | 'gemini' | 'openai'>('mock');
  let extractModel = $state('mock-v1');
  let extractStartOrder = $state('');
  let extractEndOrder = $state('');
  let extractApiKey = $state('');
  let extracting = $state(false);
  let extractError = $state<string | null>(null);
  let extractSummary = $state<ExtractStoryMemoryResponse | null>(null);

  // Add manual entity state
  let newSourceTerm = $state('');
  let newTranslatedTerm = $state('');
  let newCategory = $state<StoryEntityCategory>('Character');
  let newAliasesInput = $state('');
  let newDescription = $state('');
  let newIsLocked = $state(true);
  let newStatus = $state<FactStatus>('Confirmed');
  let creating = $state(false);
  let createError = $state<string | null>(null);

  // Inline edit entity state
  let editingId = $state<string | null>(null);
  let editSourceTerm = $state('');
  let editTranslatedTerm = $state('');
  let editCategory = $state<StoryEntityCategory>('Character');
  let editAliasesInput = $state('');
  let editDescription = $state('');
  let editIsLocked = $state(true);
  let editStatus = $state<FactStatus>('Provisional');
  let savingEdit = $state(false);
  let editError = $state<string | null>(null);

  // Manual Scene Fact form state
  let newFactKey = $state('');
  let newFactType = $state<SceneFactType>('Setting');
  let newFactSummary = $state('');
  let creatingFact = $state(false);
  let factError = $state<string | null>(null);

  // Manual Speech Observation form state
  let newSpeakerName = $state('');
  let newAddresseeName = $state('');
  let newSpeechRegister = $state('');
  let newPronounStyle = $state('');
  let newSpeechObs = $state('');
  let creatingSpeech = $state(false);
  let speechError = $state<string | null>(null);

  // Consistency check state scoped per activeJobId
  let consistencyResult = $state<{ jobId: string; report: StoryConsistencyReport } | null>(null);
  let consistencyErrorState = $state<{ jobId: string; message: string } | null>(null);
  let consistencyLoading = $state(false);
  let consistencyReport = $derived(
    consistencyResult && consistencyResult.jobId === activeJobId ? consistencyResult.report : null
  );
  let consistencyError = $derived(
    consistencyErrorState && consistencyErrorState.jobId === activeJobId ? consistencyErrorState.message : null
  );

  let filteredEntries = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    return entries.filter((item) => {
      if (categoryFilter && item.category !== categoryFilter) return false;
      if (statusFilter && item.status !== statusFilter) return false;
      if (!q) return true;
      return (
        item.sourceTerm.toLowerCase().includes(q) ||
        item.translatedTerm.toLowerCase().includes(q) ||
        (item.description?.toLowerCase().includes(q) ?? false) ||
        item.aliases.some((a) => a.toLowerCase().includes(q))
      );
    });
  });

  function statusBadgeClass(status: FactStatus): string {
    if (status === 'Confirmed') return 'bg-green-100 text-green-800 dark:bg-green-900/50 dark:text-green-300';
    if (status === 'Conflicted') return 'bg-red-100 text-red-800 dark:bg-red-900/50 dark:text-red-300';
    if (status === 'Provisional') return 'bg-blue-100 text-blue-800 dark:bg-blue-900/50 dark:text-blue-300';
    return 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300';
  }

  function parseAliases(raw: string): string[] {
    return raw.split(',').map((s) => s.trim()).filter((s) => s.length > 0);
  }

  function onExtractProviderChange(prov: 'mock' | 'gemini' | 'openai') {
    extractProvider = prov;
    if (prov === 'gemini') extractModel = 'gemini-2.5-flash';
    else if (prov === 'openai') extractModel = 'gpt-4o-mini';
    else extractModel = 'mock-v1';
  }

  async function loadAll() {
    if (!novelId || !targetLanguage) return;
    loading = true;
    error = null;
    try {
      const [g, s, o] = await Promise.all([
        getStoryMemory(novelId, { targetLanguage }),
        getSceneFacts(novelId, targetLanguage),
        getSpeechObservations(novelId, targetLanguage)
      ]);
      entries = g;
      sceneFacts = s;
      speechObservations = o;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load story memory.';
    } finally {
      loading = false;
    }
  }

  async function handleExtract(e: SubmitEvent) {
    e.preventDefault();
    if (!novelId || !targetLanguage) return;

    extracting = true;
    extractError = null;
    extractSummary = null;

    const startOrder = extractStartOrder.trim() === '' ? null : Number.parseInt(extractStartOrder, 10);
    const endOrder = extractEndOrder.trim() === '' ? null : Number.parseInt(extractEndOrder, 10);

    try {
      const result = await extractStoryMemory(novelId, {
        targetLanguage,
        provider: extractProvider,
        model: extractModel.trim() || undefined,
        startOrder: Number.isNaN(startOrder) ? null : startOrder,
        endOrder: Number.isNaN(endOrder) ? null : endOrder,
        apiKey: extractApiKey.trim() || undefined
      });
      extractApiKey = '';
      extractSummary = result;
      entries = result.glossary;
      sceneFacts = result.sceneFacts;
      speechObservations = result.speechObservations;
    } catch (err) {
      extractError = err instanceof Error ? err.message : 'Story memory extraction failed.';
    } finally {
      extracting = false;
    }
  }

  async function handleCreateEntry(e: SubmitEvent) {
    e.preventDefault();
    if (!newSourceTerm.trim() || !newTranslatedTerm.trim()) {
      createError = 'Both source term and canonical translation are required.';
      return;
    }
    creating = true;
    createError = null;
    try {
      const created = await createStoryEntity(novelId, {
        targetLanguage,
        sourceTerm: newSourceTerm.trim(),
        translatedTerm: newTranslatedTerm.trim(),
        category: newCategory,
        aliases: parseAliases(newAliasesInput),
        description: newDescription.trim() || null,
        isLocked: newIsLocked,
        status: newStatus
      });
      entries = [...entries, created];
      newSourceTerm = '';
      newTranslatedTerm = '';
      newAliasesInput = '';
      newDescription = '';
    } catch (err) {
      createError = err instanceof Error ? err.message : 'Failed to add glossary entry.';
    } finally {
      creating = false;
    }
  }

  function startEdit(item: StoryEntityItem) {
    editingId = item.id;
    editSourceTerm = item.sourceTerm;
    editTranslatedTerm = item.translatedTerm;
    editCategory = item.category;
    editAliasesInput = item.aliases.join(', ');
    editDescription = item.description ?? '';
    editIsLocked = item.isLocked;
    editStatus = item.status;
    editError = null;
  }

  async function saveEdit(item: StoryEntityItem) {
    if (!editSourceTerm.trim() || !editTranslatedTerm.trim()) {
      editError = 'Source term and canonical translation cannot be empty.';
      return;
    }
    savingEdit = true;
    editError = null;
    try {
      const updated = await updateStoryEntity(novelId, item.id, {
        targetLanguage: item.targetLanguage,
        sourceTerm: editSourceTerm.trim(),
        translatedTerm: editTranslatedTerm.trim(),
        category: editCategory,
        aliases: parseAliases(editAliasesInput),
        description: editDescription.trim() || null,
        isLocked: editIsLocked,
        status: editStatus,
        expectedVersion: item.version
      });
      entries = entries.map((x) => (x.id === item.id ? updated : x));
      editingId = null;
    } catch (err) {
      editError = err instanceof Error ? err.message : 'Failed to save changes.';
    } finally {
      savingEdit = false;
    }
  }

  async function patchEntity(item: StoryEntityItem, changes: Partial<{ isLocked: boolean; status: FactStatus }>) {
    error = null;
    try {
      const updated = await updateStoryEntity(novelId, item.id, {
        targetLanguage: item.targetLanguage,
        sourceTerm: item.sourceTerm,
        translatedTerm: item.translatedTerm,
        category: item.category,
        aliases: item.aliases,
        description: item.description ?? null,
        isLocked: changes.isLocked ?? item.isLocked,
        status: changes.status ?? item.status,
        expectedVersion: item.version
      });
      entries = entries.map((x) => (x.id === item.id ? updated : x));
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to update entity.';
    }
  }

  async function handleDeleteEntity(item: StoryEntityItem) {
    error = null;
    try {
      await deleteStoryEntity(novelId, item.id);
      entries = entries.filter((x) => x.id !== item.id);
      if (editingId === item.id) editingId = null;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to delete entry.';
    }
  }

  async function handleCreateSceneFact(e: SubmitEvent) {
    e.preventDefault();
    if (!newFactKey.trim() || !newFactSummary.trim()) {
      factError = 'Fact key and summary are required.';
      return;
    }
    creatingFact = true;
    factError = null;
    try {
      const created = await createSceneFact(novelId, {
        targetLanguage,
        factKey: newFactKey.trim(),
        summary: newFactSummary.trim(),
        factType: newFactType,
        status: 'Confirmed',
        isLocked: true
      });
      sceneFacts = [...sceneFacts, created];
      newFactKey = '';
      newFactSummary = '';
    } catch (err) {
      factError = err instanceof Error ? err.message : 'Failed to create scene fact.';
    } finally {
      creatingFact = false;
    }
  }

  async function patchSceneFact(fact: SceneFactItem, changes: Partial<{ isLocked: boolean; status: FactStatus }>) {
    factError = null;
    try {
      const updated = await updateSceneFact(novelId, fact.id, {
        targetLanguage: fact.targetLanguage,
        factKey: fact.factKey,
        summary: fact.summary,
        factType: fact.factType,
        isLocked: changes.isLocked ?? fact.isLocked,
        status: changes.status ?? fact.status,
        expectedVersion: fact.version
      });
      sceneFacts = sceneFacts.map((f) => (f.id === fact.id ? updated : f));
    } catch (err) {
      factError = err instanceof Error ? err.message : 'Failed to update scene fact.';
    }
  }

  async function handleDeleteSceneFact(fact: SceneFactItem) {
    factError = null;
    try {
      await deleteSceneFact(novelId, fact.id);
      sceneFacts = sceneFacts.filter((f) => f.id !== fact.id);
    } catch (err) {
      factError = err instanceof Error ? err.message : 'Failed to delete scene fact.';
    }
  }

  async function handleCreateSpeechObs(e: SubmitEvent) {
    e.preventDefault();
    if (!newSpeakerName.trim() || !newSpeechRegister.trim() || !newSpeechObs.trim()) {
      speechError = 'Speaker, register, and observation are required.';
      return;
    }
    creatingSpeech = true;
    speechError = null;
    try {
      const created = await createSpeechObservation(novelId, {
        targetLanguage,
        speakerName: newSpeakerName.trim(),
        addresseeName: newAddresseeName.trim() || null,
        speechRegister: newSpeechRegister.trim(),
        pronounStyle: newPronounStyle.trim() || null,
        observation: newSpeechObs.trim(),
        status: 'Confirmed',
        isLocked: true
      });
      speechObservations = [...speechObservations, created];
      newSpeakerName = '';
      newAddresseeName = '';
      newSpeechRegister = '';
      newPronounStyle = '';
      newSpeechObs = '';
    } catch (err) {
      speechError = err instanceof Error ? err.message : 'Failed to add speech observation.';
    } finally {
      creatingSpeech = false;
    }
  }

  async function patchSpeechObs(obs: SpeechObservationItem, changes: Partial<{ isLocked: boolean; status: FactStatus }>) {
    speechError = null;
    try {
      const updated = await updateSpeechObservation(novelId, obs.id, {
        targetLanguage: obs.targetLanguage,
        speakerName: obs.speakerName,
        addresseeName: obs.addresseeName ?? null,
        speechRegister: obs.speechRegister,
        pronounStyle: obs.pronounStyle ?? null,
        observation: obs.observation,
        isLocked: changes.isLocked ?? obs.isLocked,
        status: changes.status ?? obs.status,
        expectedVersion: obs.version
      });
      speechObservations = speechObservations.map((o) => (o.id === obs.id ? updated : o));
    } catch (err) {
      speechError = err instanceof Error ? err.message : 'Failed to update speech observation.';
    }
  }

  async function handleDeleteSpeechObs(obs: SpeechObservationItem) {
    speechError = null;
    try {
      await deleteSpeechObservation(novelId, obs.id);
      speechObservations = speechObservations.filter((o) => o.id !== obs.id);
    } catch (err) {
      speechError = err instanceof Error ? err.message : 'Failed to delete speech observation.';
    }
  }

  async function handleConsistencyCheck() {
    if (!activeJobId) return;
    const jobIdAtCall = activeJobId;
    consistencyLoading = true;
    consistencyErrorState = null;
    try {
      const report = await getJobConsistency(jobIdAtCall);
      consistencyResult = { jobId: jobIdAtCall, report };
    } catch (err) {
      consistencyErrorState = {
        jobId: jobIdAtCall,
        message: err instanceof Error ? err.message : 'Consistency check failed.'
      };
    } finally {
      consistencyLoading = false;
    }
  }

  onMount(() => {
    void loadAll();
  });
</script>

<section class="rounded-xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 p-6 shadow-sm space-y-6">
  <div class="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 dark:border-gray-800 pb-4">
    <div>
      <h2 class="text-xl font-semibold">Story Memory Lite & Analysis (BASI-004)</h2>
      <p class="text-sm text-gray-600 dark:text-gray-400">
        Evidence-backed entities, scene continuity facts, and speech observations for <strong>{novelTitle}</strong> ({targetLanguage.toUpperCase()}). AI extractions enter as <em>Provisional</em> until confirmed.
      </p>
    </div>
    <button
      type="button"
      onclick={loadAll}
      disabled={loading}
      class="px-3 py-1.5 text-xs font-medium rounded-lg border border-gray-300 dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors disabled:opacity-50"
    >
      {loading ? 'Refreshing...' : `Refresh (${entries.length}/${sceneFacts.length}/${speechObservations.length})`}
    </button>
  </div>

  <!-- AI / Mock Extraction Panel -->
  <form onsubmit={handleExtract} class="p-4 rounded-xl bg-gray-50 dark:bg-gray-800/50 border border-gray-200 dark:border-gray-700 space-y-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h3 class="text-sm font-semibold">Extract Story Memory from Segments</h3>
      <span class="text-xs text-gray-500">Strict evidence verification (Segment ID/order + quote); max 20 segments / 12,000 chars</span>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-4 gap-3">
      <div>
        <label for="extractProvider" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">Provider</label>
        <select
          id="extractProvider"
          value={extractProvider}
          onchange={(e) => onExtractProviderChange((e.target as HTMLSelectElement).value as 'mock' | 'gemini' | 'openai')}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs focus:ring-2 focus:ring-blue-500 focus:outline-none"
        >
          <option value="mock">Mock Provider</option>
          <option value="gemini">Google Gemini (BYOK)</option>
          <option value="openai">OpenAI (BYOK)</option>
        </select>
      </div>
      <div>
        <label for="extractModel" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">Model</label>
        <input id="extractModel" type="text" bind:value={extractModel} class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs font-mono" />
      </div>
      <div>
        <label for="extractStartOrder" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">Start Segment #</label>
        <input id="extractStartOrder" type="number" min="0" placeholder="Auto (0)" bind:value={extractStartOrder} class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
      </div>
      <div>
        <label for="extractEndOrder" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">End Segment #</label>
        <input id="extractEndOrder" type="number" min="0" placeholder="Auto window" bind:value={extractEndOrder} class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
      </div>
    </div>

    {#if extractProvider !== 'mock'}
      <div>
        <label for="extractApiKey" class="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1">
          Ephemeral Request API Key <span class="text-gray-500 font-normal">(RAM only for this request; never persisted)</span>
        </label>
        <input
          id="extractApiKey"
          type="password"
          bind:value={extractApiKey}
          placeholder={`Optional ${extractProvider.toUpperCase()} key...`}
          class="w-full px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs font-mono"
        />
      </div>
    {/if}

    {#if extractError}
      <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
        {extractError}
      </div>
    {/if}

    {#if extractSummary}
      <div class="p-2.5 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-900 rounded-lg text-xs text-green-800 dark:text-green-300">
        Extracted {extractSummary.extractedCount} items ({extractSummary.created} created as Provisional, {extractSummary.updated} updated, {extractSummary.conflicted} conflicted, {extractSummary.skippedLocked} locked skipped) · {extractSummary.totalTokens} tokens ({extractSummary.modelUsed}).
      </div>
    {/if}

    <button
      type="submit"
      disabled={extracting}
      class="px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-medium rounded-lg shadow-sm transition-colors disabled:opacity-50"
    >
      {extracting ? 'Extracting & Validating Evidence...' : 'Extract Entities, Scene Facts & Speech'}
    </button>
  </form>

  <!-- Sub-tabs: Entities | Scene Facts | Speech Observations -->
  <div class="flex flex-wrap gap-2 border-b border-gray-200 dark:border-gray-700 pb-2 text-xs font-medium">
    <button
      type="button"
      onclick={() => (activeTab = 'entities')}
      class="px-3 py-1.5 rounded-lg transition-colors {activeTab === 'entities' ? 'bg-blue-600 text-white' : 'bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300'}"
    >
      Entities / Glossary ({entries.length})
    </button>
    <button
      type="button"
      onclick={() => (activeTab = 'scenes')}
      class="px-3 py-1.5 rounded-lg transition-colors {activeTab === 'scenes' ? 'bg-blue-600 text-white' : 'bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300'}"
    >
      Scene Facts ({sceneFacts.length})
    </button>
    <button
      type="button"
      onclick={() => (activeTab = 'speech')}
      class="px-3 py-1.5 rounded-lg transition-colors {activeTab === 'speech' ? 'bg-blue-600 text-white' : 'bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300'}"
    >
      Speech Observations ({speechObservations.length})
    </button>
  </div>

  {#if error}
    <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
      {error}
    </div>
  {/if}

  {#if activeTab === 'entities'}
    <!-- Manual Add Entity -->
    <form onsubmit={handleCreateEntry} class="p-4 rounded-xl border border-gray-200 dark:border-gray-700 space-y-3">
      <h3 class="text-sm font-semibold">Add Canonical Entity Entry</h3>
      <div class="grid grid-cols-1 sm:grid-cols-4 gap-3">
        <input type="text" bind:value={newSourceTerm} placeholder="Source term (e.g. アインズ)" maxlength="256" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <input type="text" bind:value={newTranslatedTerm} placeholder="Canonical translation" maxlength="256" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <select bind:value={newCategory} class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs">
          {#each CATEGORIES as cat (cat)}
            <option value={cat}>{cat}</option>
          {/each}
        </select>
        <select bind:value={newStatus} class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs">
          {#each FACT_STATUSES as st (st)}
            <option value={st}>{st}</option>
          {/each}
        </select>
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <input type="text" bind:value={newAliasesInput} placeholder="Aliases (comma-separated)" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <input type="text" bind:value={newDescription} placeholder="Context note (tone, gender, lore)" maxlength="1000" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
      </div>
      <div class="flex flex-wrap items-center justify-between gap-3">
        <label class="inline-flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300">
          <input type="checkbox" bind:checked={newIsLocked} />
          Lock entry
        </label>
        <button type="submit" disabled={creating} class="px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-xs font-medium rounded-lg disabled:opacity-50">
          {creating ? 'Adding...' : 'Add Entry'}
        </button>
      </div>
      {#if createError}
        <p class="text-xs text-red-600 dark:text-red-400">{createError}</p>
      {/if}
    </form>

    <!-- Filter & Glossary List -->
    <div class="space-y-3">
      <div class="flex flex-wrap items-center gap-2">
        <input type="search" bind:value={searchQuery} placeholder="Search terms, aliases, notes..." class="flex-1 min-w-[200px] px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <select bind:value={categoryFilter} class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs">
          <option value="">All Categories</option>
          {#each CATEGORIES as cat (cat)}
            <option value={cat}>{cat}</option>
          {/each}
        </select>
        <select bind:value={statusFilter} class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs">
          <option value="">All Statuses</option>
          {#each FACT_STATUSES as st (st)}
            <option value={st}>{st}</option>
          {/each}
        </select>
      </div>

      {#if filteredEntries.length === 0}
        <div class="p-6 text-center text-xs text-gray-500 border border-dashed border-gray-200 dark:border-gray-700 rounded-lg">
          No entity entries matched.
        </div>
      {:else}
        <div class="divide-y divide-gray-100 dark:divide-gray-800 rounded-lg border border-gray-200 dark:border-gray-700 max-h-[380px] overflow-y-auto">
          {#each filteredEntries as item (item.id)}
            <div class="p-3.5 text-xs space-y-2 hover:bg-gray-50/50 dark:hover:bg-gray-800/30">
              {#if editingId === item.id}
                <div class="space-y-2">
                  <div class="grid grid-cols-1 sm:grid-cols-4 gap-2">
                    <input type="text" bind:value={editSourceTerm} class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs" />
                    <input type="text" bind:value={editTranslatedTerm} class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs" />
                    <select bind:value={editCategory} class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs">
                      {#each CATEGORIES as cat (cat)}
                        <option value={cat}>{cat}</option>
                      {/each}
                    </select>
                    <select bind:value={editStatus} class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs">
                      {#each FACT_STATUSES as st (st)}
                        <option value={st}>{st}</option>
                      {/each}
                    </select>
                  </div>
                  <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
                    <input type="text" bind:value={editAliasesInput} placeholder="Aliases" class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs" />
                    <input type="text" bind:value={editDescription} placeholder="Context note" class="px-2.5 py-1 border border-gray-300 dark:border-gray-700 rounded bg-white dark:bg-gray-800 text-xs" />
                  </div>
                  {#if editError}
                    <p class="text-red-600 dark:text-red-400">{editError}</p>
                  {/if}
                  <div class="flex items-center justify-between">
                    <label class="inline-flex items-center gap-1.5">
                      <input type="checkbox" bind:checked={editIsLocked} />
                      Locked
                    </label>
                    <div class="flex gap-2">
                      <button type="button" onclick={() => (editingId = null)} class="px-2.5 py-1 rounded border border-gray-300 dark:border-gray-700">Cancel</button>
                      <button type="button" disabled={savingEdit} onclick={() => saveEdit(item)} class="px-2.5 py-1 rounded bg-blue-600 text-white disabled:opacity-50">
                        {savingEdit ? 'Saving...' : 'Save'}
                      </button>
                    </div>
                  </div>
                </div>
              {:else}
                <div class="flex flex-wrap items-start justify-between gap-2">
                  <div class="space-y-1">
                    <div class="flex flex-wrap items-center gap-1.5">
                      <span class="font-semibold text-sm text-gray-900 dark:text-gray-100">{item.sourceTerm}</span>
                      <span class="text-gray-400">-&gt;</span>
                      <span class="font-semibold text-sm text-blue-700 dark:text-blue-400">{item.translatedTerm}</span>
                      <span class="px-1.5 py-0.5 rounded bg-gray-100 dark:bg-gray-800 text-[11px]">{item.category}</span>
                      <span class="px-1.5 py-0.5 rounded text-[11px] {statusBadgeClass(item.status)}">{item.status}</span>
                      <span class="px-1.5 py-0.5 rounded bg-purple-100 text-purple-800 dark:bg-purple-900/50 dark:text-purple-300 text-[11px]">{item.origin}</span>
                      {#if item.isLocked}
                        <span class="px-1.5 py-0.5 rounded bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-300 text-[11px]">Locked</span>
                      {/if}
                    </div>
                    {#if item.aliases.length > 0}
                      <div class="text-gray-500">Aliases: {item.aliases.join(', ')}</div>
                    {/if}
                    {#if item.evidenceSegmentOrder !== null && item.evidenceSegmentOrder !== undefined}
                      <div class="text-gray-500 font-mono text-[11px]">
                        Evidence: Seg #{item.evidenceSegmentOrder}{#if item.evidenceQuote} — "{item.evidenceQuote}"{/if}
                      </div>
                    {/if}
                    {#if item.conflictDetails}
                      <div class="text-red-600 dark:text-red-400">Conflict: {item.conflictDetails}</div>
                    {/if}
                    {#if item.description}
                      <div class="text-gray-600 dark:text-gray-300 italic">{item.description}</div>
                    {/if}
                  </div>
                  <div class="flex items-center gap-2">
                    {#if item.status !== 'Confirmed'}
                      <button type="button" onclick={() => patchEntity(item, { status: 'Confirmed' })} class="text-green-600 hover:underline">Confirm</button>
                    {/if}
                    <button type="button" onclick={() => patchEntity(item, { isLocked: !item.isLocked })} class="text-amber-600 hover:underline">
                      {item.isLocked ? 'Unlock' : 'Lock'}
                    </button>
                    <button type="button" onclick={() => startEdit(item)} class="text-blue-600 hover:underline">Edit</button>
                    <button type="button" onclick={() => handleDeleteEntity(item)} class="text-red-600 hover:underline">Delete</button>
                  </div>
                </div>
              {/if}
            </div>
          {/each}
        </div>
      {/if}
    </div>

  {:else if activeTab === 'scenes'}
    <form onsubmit={handleCreateSceneFact} class="p-4 rounded-xl border border-gray-200 dark:border-gray-700 space-y-3">
      <h3 class="text-sm font-semibold">Add Scene Continuity Fact</h3>
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <input type="text" bind:value={newFactKey} placeholder="Fact key (e.g. Location: Throne Room)" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <select bind:value={newFactType} class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs">
          {#each FACT_TYPES as ft (ft)}
            <option value={ft}>{ft}</option>
          {/each}
        </select>
        <input type="text" bind:value={newFactSummary} placeholder="Summary of scene state" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
      </div>
      {#if factError}<p class="text-xs text-red-600">{factError}</p>{/if}
      <button type="submit" disabled={creatingFact} class="px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-xs font-medium rounded-lg">
        {creatingFact ? 'Adding...' : 'Add Scene Fact'}
      </button>
    </form>

    {#if sceneFacts.length === 0}
      <div class="p-6 text-center text-xs text-gray-500 border border-dashed border-gray-200 dark:border-gray-700 rounded-lg">No scene facts yet.</div>
    {:else}
      <div class="divide-y divide-gray-100 dark:divide-gray-800 rounded-lg border border-gray-200 dark:border-gray-700 max-h-[340px] overflow-y-auto">
        {#each sceneFacts as fact (fact.id)}
          <div class="p-3.5 text-xs flex flex-wrap justify-between gap-2">
            <div class="space-y-1">
              <div class="flex flex-wrap items-center gap-1.5">
                <span class="font-semibold text-gray-900 dark:text-gray-100">{fact.factKey}</span>
                <span class="px-1.5 py-0.5 rounded bg-gray-100 dark:bg-gray-800 text-[11px]">{fact.factType}</span>
                <span class="px-1.5 py-0.5 rounded text-[11px] {statusBadgeClass(fact.status)}">{fact.status}</span>
                {#if fact.isLocked}<span class="px-1.5 py-0.5 rounded bg-amber-100 text-amber-800 text-[11px]">Locked</span>{/if}
              </div>
              <div class="text-gray-700 dark:text-gray-300">{fact.summary}</div>
              {#if fact.evidenceSegmentOrder !== null && fact.evidenceSegmentOrder !== undefined}
                <div class="text-gray-500 font-mono text-[11px]">Evidence: Seg #{fact.evidenceSegmentOrder}{#if fact.evidenceQuote} — "{fact.evidenceQuote}"{/if}</div>
              {/if}
              {#if fact.conflictDetails}
                <div class="text-red-600">Conflict: {fact.conflictDetails}</div>
              {/if}
            </div>
            <div class="flex items-center gap-2">
              {#if fact.status !== 'Confirmed'}
                <button type="button" onclick={() => patchSceneFact(fact, { status: 'Confirmed' })} class="text-green-600 hover:underline">Confirm</button>
              {/if}
              <button type="button" onclick={() => patchSceneFact(fact, { isLocked: !fact.isLocked })} class="text-amber-600 hover:underline">{fact.isLocked ? 'Unlock' : 'Lock'}</button>
              <button type="button" onclick={() => handleDeleteSceneFact(fact)} class="text-red-600 hover:underline">Delete</button>
            </div>
          </div>
        {/each}
      </div>
    {/if}

  {:else}
    <form onsubmit={handleCreateSpeechObs} class="p-4 rounded-xl border border-gray-200 dark:border-gray-700 space-y-3">
      <h3 class="text-sm font-semibold">Add Speech & Register Observation</h3>
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <input type="text" bind:value={newSpeakerName} placeholder="Speaker name" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <input type="text" bind:value={newAddresseeName} placeholder="Addressee (optional)" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <input type="text" bind:value={newSpeechRegister} placeholder="Register (e.g. Formal Sovereign)" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <input type="text" bind:value={newPronounStyle} placeholder="Pronoun style (e.g. Ta / Ngươi)" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
        <input type="text" bind:value={newSpeechObs} placeholder="Speech style observation" class="px-3 py-1.5 border border-gray-300 dark:border-gray-700 rounded-lg bg-white dark:bg-gray-800 text-xs" />
      </div>
      {#if speechError}<p class="text-xs text-red-600">{speechError}</p>{/if}
      <button type="submit" disabled={creatingSpeech} class="px-4 py-1.5 bg-blue-600 hover:bg-blue-700 text-white text-xs font-medium rounded-lg">
        {creatingSpeech ? 'Adding...' : 'Add Speech Observation'}
      </button>
    </form>

    {#if speechObservations.length === 0}
      <div class="p-6 text-center text-xs text-gray-500 border border-dashed border-gray-200 dark:border-gray-700 rounded-lg">No speech observations yet.</div>
    {:else}
      <div class="divide-y divide-gray-100 dark:divide-gray-800 rounded-lg border border-gray-200 dark:border-gray-700 max-h-[340px] overflow-y-auto">
        {#each speechObservations as obs (obs.id)}
          <div class="p-3.5 text-xs flex flex-wrap justify-between gap-2">
            <div class="space-y-1">
              <div class="flex flex-wrap items-center gap-1.5">
                <span class="font-semibold text-gray-900 dark:text-gray-100">{obs.speakerName}{#if obs.addresseeName} -&gt; {obs.addresseeName}{/if}</span>
                <span class="px-1.5 py-0.5 rounded bg-gray-100 dark:bg-gray-800 text-[11px]">{obs.speechRegister}</span>
                <span class="px-1.5 py-0.5 rounded text-[11px] {statusBadgeClass(obs.status)}">{obs.status}</span>
                {#if obs.isLocked}<span class="px-1.5 py-0.5 rounded bg-amber-100 text-amber-800 text-[11px]">Locked</span>{/if}
              </div>
              <div class="text-gray-700 dark:text-gray-300">{#if obs.pronounStyle}<strong>Pronouns:</strong> {obs.pronounStyle} · {/if}{obs.observation}</div>
              {#if obs.evidenceSegmentOrder !== null && obs.evidenceSegmentOrder !== undefined}
                <div class="text-gray-500 font-mono text-[11px]">Evidence: Seg #{obs.evidenceSegmentOrder}{#if obs.evidenceQuote} — "{obs.evidenceQuote}"{/if}</div>
              {/if}
              {#if obs.conflictDetails}
                <div class="text-red-600">Conflict: {obs.conflictDetails}</div>
              {/if}
            </div>
            <div class="flex items-center gap-2">
              {#if obs.status !== 'Confirmed'}
                <button type="button" onclick={() => patchSpeechObs(obs, { status: 'Confirmed' })} class="text-green-600 hover:underline">Confirm</button>
              {/if}
              <button type="button" onclick={() => patchSpeechObs(obs, { isLocked: !obs.isLocked })} class="text-amber-600 hover:underline">{obs.isLocked ? 'Unlock' : 'Lock'}</button>
              <button type="button" onclick={() => handleDeleteSpeechObs(obs)} class="text-red-600 hover:underline">Delete</button>
            </div>
          </div>
        {/each}
      </div>
    {/if}
  {/if}

  <!-- Consistency Check Diagnostic -->
  {#if activeJobId}
    <div class="pt-4 border-t border-gray-100 dark:border-gray-800 space-y-3">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 class="text-sm font-semibold">Entity Consistency Check</h3>
          <p class="text-xs text-gray-500">Compare active job segments against Confirmed/Provisional glossary entries.</p>
        </div>
        <button
          type="button"
          onclick={handleConsistencyCheck}
          disabled={consistencyLoading}
          class="px-3 py-1.5 text-xs font-medium rounded-lg border border-blue-300 dark:border-blue-800 text-blue-700 dark:text-blue-300 hover:bg-blue-50 dark:hover:bg-blue-950/30 transition-colors disabled:opacity-50"
        >
          {consistencyLoading ? 'Checking...' : 'Check Consistency'}
        </button>
      </div>

      {#if consistencyError}
        <div class="p-2.5 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-900 rounded-lg text-xs text-red-700 dark:text-red-400">
          {consistencyError}
        </div>
      {/if}

      {#if consistencyReport}
        {#if consistencyReport.issueCount === 0}
          <div class="p-3 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-900 rounded-lg text-xs text-green-800 dark:text-green-300">
            Checked {consistencyReport.checkedSegments} completed segments against {consistencyReport.glossaryCount} glossary entries: 0 consistency issues found.
          </div>
        {:else}
          <div class="p-3 bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-900 rounded-lg text-xs text-amber-900 dark:text-amber-200 space-y-1.5">
            <div class="font-semibold">
              Found {consistencyReport.issueCount} potential consistency mismatch(es) across {consistencyReport.checkedSegments} segments:
            </div>
            <ul class="list-disc pl-5 space-y-1 max-h-40 overflow-y-auto">
              {#each consistencyReport.issues as issue (`${issue.segmentOrder}-${issue.entityId}`)}
                <li>
                  Segment #{issue.segmentOrder}: matched <strong>{issue.matchedSourceTerms.join(', ')}</strong> [{issue.category}], expected canonical translation <strong>{issue.expectedTranslation}</strong>.
                </li>
              {/each}
            </ul>
          </div>
        {/if}
      {/if}
    </div>
  {/if}
</section>
