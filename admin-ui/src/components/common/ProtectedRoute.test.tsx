import { render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, it, expect, vi } from 'vitest';
import ProtectedRoute from './ProtectedRoute';
import { useAuth } from '../../context/AuthContext';

vi.mock('../../context/AuthContext', () => ({
  useAuth: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);

function renderProtectedRoute() {
  return render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <Routes>
        <Route
          path="/dashboard"
          element={
            <ProtectedRoute>
              <div>Secret Dashboard</div>
            </ProtectedRoute>
          }
        />
        <Route path="/login" element={<div>Login Page</div>} />
      </Routes>
    </MemoryRouter>
  );
}

describe('ProtectedRoute', () => {
  it('shows a loading state while auth status is being determined', () => {
    mockedUseAuth.mockReturnValue({
      isAuthenticated: false,
      loading: true,
      user: null,
      login: vi.fn(),
      logout: vi.fn(),
      setCurrentUser: vi.fn(),
    });

    renderProtectedRoute();

    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('redirects to /login when the user is not authenticated', () => {
    mockedUseAuth.mockReturnValue({
      isAuthenticated: false,
      loading: false,
      user: null,
      login: vi.fn(),
      logout: vi.fn(),
      setCurrentUser: vi.fn(),
    });

    renderProtectedRoute();

    expect(screen.getByText('Login Page')).toBeInTheDocument();
    expect(screen.queryByText('Secret Dashboard')).not.toBeInTheDocument();
  });

  it('renders the protected children when the user is authenticated', () => {
    mockedUseAuth.mockReturnValue({
      isAuthenticated: true,
      loading: false,
      user: { id: '1' } as never,
      login: vi.fn(),
      logout: vi.fn(),
      setCurrentUser: vi.fn(),
    });

    renderProtectedRoute();

    expect(screen.getByText('Secret Dashboard')).toBeInTheDocument();
  });
});
