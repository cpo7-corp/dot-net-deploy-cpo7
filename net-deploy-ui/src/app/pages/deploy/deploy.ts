import { Component, inject, OnInit, signal, effect, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { DeployService } from '../../services/deploy.service';
import { SettingsService } from '../../services/settings.service';
import { ServiceStatus, DeployLogEntry, VpsSettings, ProjectVersion, DeploymentHistory } from '../../models/api-models';

@Component({
  selector: 'app-deploy',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './deploy.html',
  styleUrl: './deploy.less'
})
export class DeployComponent implements OnInit {
  private servicesSvc = inject(ServicesMonitorService);
  public deploySvc = inject(DeployService); // Public to access signals in HTML
  private settingsSvc = inject(SettingsService);
  private translate = inject(TranslateService);

  services = signal<ServiceStatus[]>([]);
  groupedServices = computed(() => {
    const groups = new Map<string, ServiceStatus[]>();

    for (const service of this.services()) {
      const key = (service.group || '').trim() || '__ungrouped__';
      const items = groups.get(key) ?? [];
      items.push(service);
      groups.set(key, items);
    }

    return Array.from(groups.entries())
      .map(([key, services]) => ({
        key,
        title: key === '__ungrouped__' ? 'Ungrouped' : key,
        services: [...services].sort((a, b) => {
          const leftOrder = a.order ?? Number.MAX_SAFE_INTEGER;
          const rightOrder = b.order ?? Number.MAX_SAFE_INTEGER;

          if (leftOrder !== rightOrder) {
            return leftOrder - rightOrder;
          }

          return a.name.localeCompare(b.name);
        })
      }))
      .sort((left, right) => {
        if (left.key === '__ungrouped__') return 1;
        if (right.key === '__ungrouped__') return -1;

        const leftOrder = left.services[0]?.order ?? Number.MAX_SAFE_INTEGER;
        const rightOrder = right.services[0]?.order ?? Number.MAX_SAFE_INTEGER;

        if (leftOrder !== rightOrder) {
          return leftOrder - rightOrder;
        }

        return left.title.localeCompare(right.title);
      })
      ;
  });
  environments = signal<VpsSettings[]>([]);
  selectedEnvironmentId = signal<string | null>(null);
  selectedServiceIds: Set<string> = new Set();
  serviceBranches: Record<string, string> = {}; // Stores custom branch per service
  serviceCommits: Record<string, string> = {}; // Stores custom commit hash per service (if selected)
  serviceCommitMessages: Record<string, string> = {}; // Stores selected commit message per service

  // Commit Selector State
  showCommitSelector = signal<boolean>(false);
  selectingService = signal<ServiceStatus | null>(null);
  recentCommits = signal<ProjectVersion[]>([]);
  deploymentHistory = signal<DeploymentHistory[]>([]);
  loadingCommits = signal<boolean>(false);
  loadingMoreCommits = signal<boolean>(false);
  hasMoreCommits = signal<boolean>(true);
  activeTab = signal<'git' | 'history'>('git');
  private readonly commitsPageSize = 20;

  loading = signal<boolean>(true);

  // Read signals from service
  deploying = this.deploySvc.deploying;
  logs = this.deploySvc.logs;
  deploymentProgress = this.deploySvc.deploymentProgress;
  elapsedTime = this.deploySvc.elapsedTime;
  failedServiceIds = this.deploySvc.failedServiceIds;
  isPaused = this.deploySvc.isPaused;

  constructor() {
    // Scroll to bottom when logs change
    effect(() => {
      this.logs();
      this.scrollToBottom();
    });
  }

  ngOnInit() {
    this.servicesSvc.getAll().subscribe({
      next: (data) => {
        this.services.set(data);
        this.updateDefaultBranches();
        this.loading.set(false);
      }
    });

    this.settingsSvc.getSettings().subscribe(data => {
      this.environments.set(data.vpsEnvironments || []);
      const savedEnvId = localStorage.getItem('lastEnvironmentId');
      const environmentExists = data.vpsEnvironments?.some(e => e.id === savedEnvId);

      if (savedEnvId && environmentExists) {
        this.selectedEnvironmentId.set(savedEnvId);
      } else if (data.vpsEnvironments?.length > 0) {
        this.selectedEnvironmentId.set(data.vpsEnvironments[0].id || null);
      }
      this.updateDefaultBranches();
    });
  }

  onEnvironmentChange(id: string | null) {
    if (id) {
      localStorage.setItem('lastEnvironmentId', id);
    }
    this.updateDefaultBranches();
  }

  private updateDefaultBranches() {
    const envId = this.selectedEnvironmentId();
    this.services().forEach(s => {
      if (s.id) {
        const envCfg = s.environments?.find(e => e.environmentId === envId);
        this.serviceBranches[s.id] = envCfg?.defaultBranch || 'main';
      }
    });
  }

  toggleService(id: string) {
    if (this.selectedServiceIds.has(id)) {
      this.selectedServiceIds.delete(id);
    } else {
      this.selectedServiceIds.add(id);
    }
  }

  selectAll() {
    this.services().forEach(s => {
      if (s.id) this.selectedServiceIds.add(s.id);
    });
  }

  clearSelection() {
    this.selectedServiceIds.clear();
  }

  startDeploy(configs?: { serviceId: string, branch: string }[], forceClean: boolean = true, retryMode?: number) {
    if (!configs && this.selectedServiceIds.size === 0) return;

    const deploymentConfigs = configs || Array.from(this.selectedServiceIds).map(id => ({
      serviceId: id,
      branch: this.serviceCommits[id] ? this.serviceCommits[id] : (this.serviceBranches[id] || 'main')
    }));

    const pull = retryMode === 3 ? false : this.deploySvc.deployPull();
    const build = retryMode === 3 ? false : this.deploySvc.deployBuild();
    const deploy = retryMode === 3 ? true : this.deploySvc.deployTransfer();

    this.deploySvc.startDeployment(
      deploymentConfigs, 
      this.selectedEnvironmentId(), 
      forceClean, 
      pull, 
      build, 
      deploy
    );
  }

  retryFailed() {
    const failedIds = this.failedServiceIds();
    if (failedIds.length === 0) return;

    const configs = failedIds.map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    this.startDeploy(configs, false, 3);
  }

  stopDeployment() {
    const sid = this.deploySvc.currentSessionId();
    if (sid) {
      this.deploySvc.stop(sid).subscribe();
    }
  }

  togglePause() {
    const sid = this.deploySvc.currentSessionId();
    if (!sid) return;

    if (this.isPaused()) {
      this.deploySvc.resume(sid).subscribe(() => this.deploySvc.isPaused.set(false));
    } else {
      this.deploySvc.pause(sid).subscribe(() => this.deploySvc.isPaused.set(true));
    }
  }

  getServiceName(id: string) { 
    return this.services().find(s => s.id === id)?.name || 'Unknown'; 
  }

  getLogClass(level: string): string {
    return `log-${level.toLowerCase()}`;
  }

  objectKeys(obj: any) { 
    return Object.keys(obj); 
  }

  getCurrentVersion(service: any) {
    if (!service.environments || !this.selectedEnvironmentId()) return null;
    return service.environments.find((e: any) => e.environmentId === this.selectedEnvironmentId())?.currentVersion;
  }

  openCommitSelector(service: ServiceStatus) {
    this.selectingService.set(service);
    this.showCommitSelector.set(true);
    this.activeTab.set('git');
    this.fetchCommits(true);
    this.fetchHistory();
  }

  closeCommitSelector() {
    this.showCommitSelector.set(false);
    this.selectingService.set(null);
  }

  fetchCommits(reset: boolean = false) {
    const s = this.selectingService();
    if (!s) return;

    if (reset) {
      this.recentCommits.set([]);
      this.hasMoreCommits.set(true);
    }

    if (!reset && (!this.hasMoreCommits() || this.loadingCommits() || this.loadingMoreCommits())) {
      return;
    }

    const branch = this.serviceBranches[s.id!] || 'main';
    const skip = reset ? 0 : this.recentCommits().length;

    if (reset) {
      this.loadingCommits.set(true);
    } else {
      this.loadingMoreCommits.set(true);
    }

    this.deploySvc.getCommits(s.repoUrl, branch, skip, this.commitsPageSize).subscribe({
      next: (result) => {
        this.recentCommits.set(reset ? result.items : [...this.recentCommits(), ...result.items]);
        this.hasMoreCommits.set(result.hasMore);
        this.loadingCommits.set(false);
        this.loadingMoreCommits.set(false);
      },
      error: () => {
        this.loadingCommits.set(false);
        this.loadingMoreCommits.set(false);
      }
    });
  }

  fetchHistory() {
    const s = this.selectingService();
    const envId = this.selectedEnvironmentId();
    if (!s || !envId) return;
    this.deploySvc.getHistory(s.id!, envId).subscribe(history => {
      this.deploymentHistory.set(history);
    });
  }

  selectCommit(commit: ProjectVersion) {
    const s = this.selectingService();
    if (!s) return;
    this.serviceCommits[s.id!] = commit.commitHash;
    this.serviceCommitMessages[s.id!] = commit.commitMessage;
    this.serviceBranches[s.id!] = commit.branch || this.serviceBranches[s.id!] || 'main';
    this.closeCommitSelector();
  }

  selectFromHistory(item: DeploymentHistory) {
    const s = this.selectingService();
    if (!s) return;
    this.serviceCommits[s.id!] = item.version.commitHash;
    this.serviceCommitMessages[s.id!] = item.version.commitMessage;
    this.serviceBranches[s.id!] = item.version.branch || this.serviceBranches[s.id!] || 'main';
    this.closeCommitSelector();
  }

  updateServiceBranch(serviceId: string, branch: string) {
    this.serviceBranches[serviceId] = branch;
    delete this.serviceCommits[serviceId];
    delete this.serviceCommitMessages[serviceId];
  }

  onCommitListScroll(event: Event) {
    if (this.activeTab() !== 'git' || !this.showCommitSelector()) return;

    const element = event.target as HTMLElement | null;
    if (!element) return;

    const threshold = 80;
    const remaining = element.scrollHeight - element.scrollTop - element.clientHeight;
    if (remaining <= threshold) {
      this.fetchCommits();
    }
  }

  getSelectedCommitHash(serviceId: string): string | null {
    return this.serviceCommits[serviceId] || null;
  }

  getSelectedCommitMessage(serviceId: string): string | null {
    return this.serviceCommitMessages[serviceId] || null;
  }

  getRepoCommitUrl(repoUrl: string, commitHash: string): string | null {
    const normalizedRepoUrl = this.normalizeRepoUrl(repoUrl);
    if (!normalizedRepoUrl || !commitHash) return null;

    try {
      const url = new URL(normalizedRepoUrl);
      const encodedCommitHash = encodeURIComponent(commitHash);

      if (url.hostname.includes('gitlab')) {
        return `${normalizedRepoUrl}/-/commit/${encodedCommitHash}`;
      }

      if (url.hostname.includes('bitbucket')) {
        return `${normalizedRepoUrl}/commits/${encodedCommitHash}`;
      }

      return `${normalizedRepoUrl}/commit/${encodedCommitHash}`;
    } catch {
      return null;
    }
  }

  private normalizeRepoUrl(repoUrl: string): string | null {
    if (!repoUrl) return null;

    const trimmed = repoUrl.trim();
    const blobIndex = trimmed.indexOf('/blob/');
    const treeIndex = trimmed.indexOf('/tree/');
    const splitIndex = blobIndex >= 0 ? blobIndex : treeIndex;
    const repoOnly = splitIndex >= 0 ? trimmed.substring(0, splitIndex) : trimmed;
    const withoutGitSuffix = repoOnly.replace(/\.git$/i, '');

    return withoutGitSuffix || null;
  }

  private scrollToBottom() {
    setTimeout(() => {
      const el = document.getElementById('logContainer');
      if (el) el.scrollTop = el.scrollHeight;
    }, 50);
  }
}
