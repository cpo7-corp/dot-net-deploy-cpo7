import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { 
    path: 'dashboard', 
    loadComponent: () => import('./pages/dashboard/dashboard').then(c => c.DashboardComponent) 
  },
  { 
    path: 'deploy', 
    loadComponent: () => import('./pages/deploy/deploy').then(c => c.DeployComponent) 
  },
  { 
    path: 'logs', 
    loadComponent: () => import('./pages/logs/logs').then(c => c.LogsComponent) 
  },
  { 
    path: 'services', 
    loadComponent: () => import('./pages/services/services').then(c => c.ServicesComponent) 
  },
  { 
    path: 'settings', 
    loadComponent: () => import('./pages/settings/settings').then(c => c.SettingsComponent) 
  }
];
