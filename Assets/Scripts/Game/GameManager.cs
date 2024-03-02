using System;
using Tuntenfisch.Generics;
using UnityEngine;

namespace Tuntenfisch.Game
{
    public class GameManager : SingletonComponent<GameManager>
    {
        
        private void Start()
        {
            Application.targetFrameRate = 60;
        }

        private void Update()
        {
            
        }
    }
}